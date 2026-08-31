// -----------------------------------------------------------------------
//  GhModels.cs
//  Plain data objects returned by GhInspector.
//  No GH types here — only BCL types (string, int, double, List<>).
//  This keeps the models serialization-friendly and dependency-free.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace WASPer_3DP.AI
{
    // ------------------------------------------------------------------
    //  Full canvas snapshot — the top-level object written to disk
    // ------------------------------------------------------------------

    /// <summary>
    /// Complete point-in-time snapshot of the active canvas.
    /// This is what GhCapture serializes to JSON and writes to disk
    /// so Claude can read it directly from the file system.
    /// </summary>
    public class GhSnapshot
    {
        /// <summary>UTC timestamp of when the snapshot was taken.</summary>
        public DateTime CapturedAt { get; set; }

        /// <summary>Canvas-level counts and health summary.</summary>
        public GhCanvasSummary Summary { get; set; }

        /// <summary>All active runtime warnings and errors on the canvas.</summary>
        public List<GhRuntimeIssue> Issues { get; set; }

        /// <summary>Full detail of every currently selected object, including script content.</summary>
        public List<GhComponentInfo> SelectedComponents { get; set; }

        /// <summary>
        /// All number sliders on the canvas with their current values and ranges.
        /// This is what Claude reads to find a slider's GUID before writing a command.
        /// </summary>
        public List<GhSliderInfo> Sliders { get; set; }

        /// <summary>
        /// All GH_GenePool components on the canvas.
        /// Each entry lists every gene with its current value and allowed range.
        /// Claude uses this to build a SetGeneValues command.
        /// </summary>
        public List<GhGenePoolInfo> GenePools { get; set; }

        /// <summary>
        /// Complete map of every object on the canvas — components, panels,
        /// sliders, and standalone params — with their input/output param names
        /// and wire connections.  Claude reads this to understand the canvas
        /// topology and plan where to place and connect new components.
        /// Groups and pure annotation objects are excluded to keep the list lean.
        /// </summary>
        public List<GhObjectSnapshot> AllObjects { get; set; }
    }


    // ------------------------------------------------------------------
    //  Canvas-level summary
    // ------------------------------------------------------------------

    /// <summary>
    /// Top-level snapshot of the active Grasshopper canvas.
    /// </summary>
    public class GhCanvasSummary
    {
        /// <summary>File name shown in the GH title bar (no extension).</summary>
        public string DocumentName { get; set; }

        /// <summary>Total number of objects on the canvas (components + params + groups + annotations).</summary>
        public int ObjectCount { get; set; }

        /// <summary>Number of currently selected objects (any type).</summary>
        public int SelectedCount { get; set; }

        /// <summary>Number of objects that are proper GH components (IGH_Component).</summary>
        public int ComponentCount { get; set; }

        /// <summary>Number of components that have at least one runtime warning.</summary>
        public int WarningCount { get; set; }

        /// <summary>Number of components that have at least one runtime error.</summary>
        public int ErrorCount { get; set; }
    }

    // ------------------------------------------------------------------
    //  Runtime issue (warning or error on a specific component)
    // ------------------------------------------------------------------

    /// <summary>
    /// A single runtime message emitted by a component.
    /// One component can produce many GhRuntimeIssue entries.
    /// </summary>
    public class GhRuntimeIssue
    {
        /// <summary>Full display name of the component.</summary>
        public string ComponentName { get; set; }

        /// <summary>Short nickname shown on the canvas.</summary>
        public string ComponentNickName { get; set; }

        /// <summary>"Warning" or "Error".</summary>
        public string Level { get; set; }

        /// <summary>The raw message text from Grasshopper.</summary>
        public string Message { get; set; }
    }

    // ------------------------------------------------------------------
    //  Per-component detail (used for selected-component inspection)
    // ------------------------------------------------------------------

    /// <summary>
    /// Detailed snapshot of a single canvas object.
    /// Populated for both components and standalone parameters.
    /// </summary>
    public class GhComponentInfo
    {
        /// <summary>Instance GUID — unique per canvas placement.</summary>
        public string Id { get; set; }

        /// <summary>Full display name.</summary>
        public string Name { get; set; }

        /// <summary>Short nickname shown on the canvas.</summary>
        public string NickName { get; set; }

        /// <summary>GH tab category (e.g. "WASPer_3DP", "Params", "Maths").</summary>
        public string Category { get; set; }

        /// <summary>GH sub-category / panel (e.g. "1_Utils", "Primitive").</summary>
        public string SubCategory { get; set; }

        /// <summary>True when the object is currently selected on the canvas.</summary>
        public bool Selected { get; set; }

        /// <summary>True when the object is locked (greyed out, not solving).</summary>
        public bool Locked { get; set; }

        /// <summary>True when geometry preview is hidden for this object.</summary>
        public bool Hidden { get; set; }

        /// <summary>"OK", "Warning", "Error", or "N/A" for non-active objects.</summary>
        public string RuntimeLevel { get; set; }

        /// <summary>Canvas X coordinate of the component pivot.</summary>
        public double PivotX { get; set; }

        /// <summary>Canvas Y coordinate of the component pivot.</summary>
        public double PivotY { get; set; }

        /// <summary>Names of all input parameters (empty for standalone params).</summary>
        public List<string> InputNames { get; set; } = new List<string>();

        /// <summary>Names of all output parameters (empty for standalone params).</summary>
        public List<string> OutputNames { get; set; } = new List<string>();

        /// <summary>
        /// Source code content if this is a script component (C#, Python, etc.).
        /// Null when the object is not a script component or the content could not be read.
        /// </summary>
        public string ScriptContent { get; set; }

        /// <summary>
        /// CLR type name of the GH object (e.g. "CSharpScriptComponent").
        /// Useful for identifying script component variants without hard-coding type strings.
        /// </summary>
        public string TypeName { get; set; }
    }

    // ------------------------------------------------------------------
    //  Slider info — included in every snapshot so Claude can look up
    //  the correct GUID before writing a mutation command
    // ------------------------------------------------------------------

    /// <summary>
    /// Describes a single GH_NumberSlider on the canvas.
    /// </summary>
    public class GhSliderInfo
    {
        /// <summary>Instance GUID — use this in GhMutationCommand.TargetId.</summary>
        public string Id { get; set; }

        /// <summary>Full display name of the slider.</summary>
        public string Name { get; set; }

        /// <summary>Short nickname shown on the canvas.</summary>
        public string NickName { get; set; }

        /// <summary>Current value.</summary>
        public double CurrentValue { get; set; }

        /// <summary>Minimum allowed value.</summary>
        public double Minimum { get; set; }

        /// <summary>Maximum allowed value.</summary>
        public double Maximum { get; set; }

        /// <summary>"Float", "Integer", or "Odd" / "Even".</summary>
        public string SliderType { get; set; }

        /// <summary>Canvas X position.</summary>
        public double PivotX { get; set; }

        /// <summary>Canvas Y position.</summary>
        public double PivotY { get; set; }
    }

    // ------------------------------------------------------------------
    //  Mutation command — Claude writes this to gh_command.json
    // ------------------------------------------------------------------

    /// <summary>
    /// A single instruction for GhMutator to execute.
    /// Claude writes this file; the GH component reads and executes it.
    /// </summary>
    public class GhMutationCommand
    {
        /// <summary>
        /// Unique identifier for this command (e.g. a timestamp or short UUID).
        /// Used to detect whether the command has already been consumed.
        /// </summary>
        public string CommandId { get; set; }

        /// <summary>
        /// What to do. Currently supported: "SetSliderValue".
        /// Case-insensitive.
        /// </summary>
        public string CommandType { get; set; }

        /// <summary>
        /// Instance GUID of the target object (preferred — unambiguous).
        /// Copy from GhSliderInfo.Id in the snapshot.
        /// </summary>
        public string TargetId { get; set; }

        /// <summary>
        /// Nickname of the target slider (fallback when TargetId is not set).
        /// Matched case-insensitively. First match wins.
        /// </summary>
        public string TargetNickName { get; set; }

        /// <summary>
        /// The new value to set. Automatically clamped to [Minimum, Maximum].
        /// </summary>
        public double Value { get; set; }

        /// <summary>Optional human-readable note explaining why this change is being made.</summary>
        public string Reason { get; set; }

        // ---- Gene pool fields (used when CommandType = "SetGeneValues") ----

        /// <summary>
        /// New values for the gene pool, one entry per gene index.
        /// Index order must match the Genes list in GhGenePoolInfo.
        /// To update only specific genes, use GeneIndices together with this list.
        /// Values are clamped to each gene's [Lower, Upper] range.
        /// </summary>
        public List<double> GeneValues { get; set; }

        /// <summary>
        /// Optional zero-based indices of which genes to update.
        /// If null or empty, GeneValues are applied to ALL genes in order.
        /// If provided, GeneValues[i] is applied to gene GeneIndices[i].
        /// </summary>
        public List<int> GeneIndices { get; set; }
    }

    // ------------------------------------------------------------------
    //  Mutation result — GH component writes this to gh_result.json
    // ------------------------------------------------------------------

    /// <summary>
    /// Written to disk after a command is executed.
    /// Claude reads this to confirm whether the mutation succeeded.
    /// </summary>
    public class GhMutationResult
    {
        /// <summary>Matches GhMutationCommand.CommandId.</summary>
        public string CommandId { get; set; }

        /// <summary>UTC timestamp of execution.</summary>
        public DateTime ExecutedAt { get; set; }

        /// <summary>True if the mutation was applied successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Human-readable description of what happened (or what went wrong).</summary>
        public string Message { get; set; }

        /// <summary>Value before the change. Null on failure.</summary>
        public double? PreviousValue { get; set; }

        /// <summary>Value actually applied (may differ from requested if clamped). Null on failure.</summary>
        public double? NewValue { get; set; }
    }

    // ------------------------------------------------------------------
    //  Gene pool info — included in every snapshot
    // ------------------------------------------------------------------

    /// <summary>
    /// Describes one gene (slot) inside a GH_GenePool.
    /// </summary>
    public class GhGeneInfo
    {
        /// <summary>Zero-based index inside the pool.</summary>
        public int Index { get; set; }

        /// <summary>Current gene value.</summary>
        public double Value { get; set; }

        /// <summary>Minimum allowed value for this gene.</summary>
        public double Lower { get; set; }

        /// <summary>Maximum allowed value for this gene.</summary>
        public double Upper { get; set; }
    }

    /// <summary>
    /// Describes a GH_GenePool component on the canvas.
    /// Included in every snapshot so Claude can build a SetGeneValues command.
    /// </summary>
    public class GhGenePoolInfo
    {
        /// <summary>Instance GUID — use this in the mutation command TargetId.</summary>
        public string Id { get; set; }

        /// <summary>Full display name.</summary>
        public string Name { get; set; }

        /// <summary>Short nickname shown on the canvas.</summary>
        public string NickName { get; set; }

        /// <summary>Total number of genes in this pool.</summary>
        public int GeneCount { get; set; }

        /// <summary>Per-gene values, ranges, and indices.</summary>
        public List<GhGeneInfo> Genes { get; set; } = new List<GhGeneInfo>();

        /// <summary>Canvas X position.</summary>
        public double PivotX { get; set; }

        /// <summary>Canvas Y position.</summary>
        public double PivotY { get; set; }
    }

    // ------------------------------------------------------------------
    //  Full canvas object map — added to every snapshot
    // ------------------------------------------------------------------

    /// <summary>
    /// One end of a wire connection — identifies the upstream object and the
    /// specific output param that the wire originates from.
    /// </summary>
    public class GhWireSource
    {
        /// <summary>InstanceGuid of the upstream object (component or standalone param).</summary>
        public string SourceId   { get; set; }

        /// <summary>Name of the upstream output param (e.g. "Result", "output").</summary>
        public string SourceParam { get; set; }
    }

    /// <summary>
    /// Snapshot of a single input or output parameter slot on a component.
    /// </summary>
    public class GhParamSnapshot
    {
        /// <summary>Full parameter name (e.g. "p_points").</summary>
        public string Name     { get; set; }

        /// <summary>Short nickname shown on the canvas (e.g. "pts").</summary>
        public string NickName { get; set; }

        /// <summary>
        /// Wire sources — only populated for INPUT params.
        /// Each entry identifies the upstream object + output param feeding this slot.
        /// Empty means the param is not connected (uses its default or is optional).
        /// </summary>
        public List<GhWireSource> Sources { get; set; } = new List<GhWireSource>();
    }

    /// <summary>
    /// Snapshot of any object on the canvas (component, panel, slider, group, etc.).
    /// Gives Claude the full topology needed to plan wire connections.
    /// </summary>
    public class GhObjectSnapshot
    {
        /// <summary>Instance GUID — use this in connect_wire commands.</summary>
        public string Id          { get; set; }

        /// <summary>Full display name (e.g. "wsp_Gc02_Flow from Proximity").</summary>
        public string Name        { get; set; }

        /// <summary>Short nickname on the canvas (e.g. "ProxFlow").</summary>
        public string NickName    { get; set; }

        /// <summary>CLR type name (e.g. "GH_Panel", "CSharpScriptComponent").</summary>
        public string TypeName    { get; set; }

        /// <summary>GH tab category (e.g. "WASPer_3DP").</summary>
        public string Category    { get; set; }

        /// <summary>GH sub-category (e.g. "5_Gcode").</summary>
        public string SubCategory { get; set; }

        /// <summary>"OK", "Warning", "Error", or "N/A".</summary>
        public string RuntimeLevel { get; set; }

        /// <summary>Canvas X pivot.</summary>
        public double PivotX { get; set; }

        /// <summary>Canvas Y pivot.</summary>
        public double PivotY { get; set; }

        /// <summary>
        /// Input parameter slots (components only).
        /// Each slot lists its upstream wire sources so Claude can see what is
        /// already connected and what is still open.
        /// </summary>
        public List<GhParamSnapshot> Inputs  { get; set; } = new List<GhParamSnapshot>();

        /// <summary>Output parameter slots (components only).</summary>
        public List<GhParamSnapshot> Outputs { get; set; } = new List<GhParamSnapshot>();

        /// <summary>
        /// Current text / value for standalone containers (GH_Panel, GH_NumberSlider).
        /// Null for proper components.
        /// </summary>
        public string Value { get; set; }
    }

    // ------------------------------------------------------------------
    //  Batch command — Claude writes a list of commands in one file
    // ------------------------------------------------------------------

    /// <summary>
    /// A batch of mutation commands executed in order on a single button press.
    /// Claude writes this to gh_command.json when multiple changes are needed.
    /// The GH component detects this format automatically (it has a "Commands" array).
    /// </summary>
    public class GhBatchCommand
    {
        /// <summary>
        /// Unique identifier for this batch (e.g. "batch-2024-001").
        /// Used for tracking in the result file.
        /// </summary>
        public string BatchId { get; set; }

        /// <summary>Optional human-readable description of what this batch does.</summary>
        public string Note { get; set; }

        /// <summary>
        /// Ordered list of commands to execute.
        /// All commands are attempted; failures are reported per-command without stopping the batch.
        /// </summary>
        public List<GhMutationCommand> Commands { get; set; } = new List<GhMutationCommand>();
    }

    // ------------------------------------------------------------------
    //  Batch result — GH component writes this for batch commands
    // ------------------------------------------------------------------

    /// <summary>
    /// Written to gh_result.json after a batch command is executed.
    /// Contains one result entry per command in the batch.
    /// </summary>
    public class GhBatchResult
    {
        /// <summary>Matches GhBatchCommand.BatchId.</summary>
        public string BatchId { get; set; }

        /// <summary>UTC timestamp of when the batch started executing.</summary>
        public DateTime ExecutedAt { get; set; }

        /// <summary>True only when ALL commands in the batch succeeded.</summary>
        public bool AllSucceeded { get; set; }

        /// <summary>Number of commands that succeeded.</summary>
        public int SuccessCount { get; set; }

        /// <summary>Number of commands that failed.</summary>
        public int FailCount { get; set; }

        /// <summary>One result per command, in the same order as the batch.</summary>
        public List<GhMutationResult> Results { get; set; } = new List<GhMutationResult>();

        /// <summary>Human-readable summary (e.g. "2/2 commands succeeded").</summary>
        public string Summary { get; set; }
    }
}
