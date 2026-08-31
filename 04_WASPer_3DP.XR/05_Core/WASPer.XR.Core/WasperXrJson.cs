using System.Text.Json;
using System.Text.Json.Serialization;

namespace WASPer.XR.Core;

/// <summary>
/// Serialization (plan Phase 1 / M1 deliverable). Uses System.Text.Json,
/// which ships in the net8.0 BCL, so WASPer.XR.Core stays at zero external
/// PackageReferences -- the same constraint WASPer.LiveLink.Protocol holds
/// itself to, for the same reason: one dependency-free assembly that every
/// client (Grasshopper .gha, Gamma, a future browser server, Unity, Godot)
/// can reference without pulling anything else in.
/// </summary>
public static class WasperXrJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(WASPerPrintJob job) =>
        JsonSerializer.Serialize(job, Options);

    public static WASPerPrintJob? DeserializePrintJob(string json) =>
        JsonSerializer.Deserialize<WASPerPrintJob>(json, Options);

    public static string Serialize(WASPerProcessState state) =>
        JsonSerializer.Serialize(state, Options);

    public static WASPerProcessState? DeserializeProcessState(string json) =>
        JsonSerializer.Deserialize<WASPerProcessState>(json, Options);

    public static string Serialize(StudySnapshot study) =>
        JsonSerializer.Serialize(study, Options);
}
