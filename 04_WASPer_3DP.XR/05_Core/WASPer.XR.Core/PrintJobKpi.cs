namespace WASPer.XR.Core;

/// <summary>
/// One computed KPI value carried alongside a WASPerPrintJob (KPI-surfacing
/// feature, following M3's playback controls). This is a plain-data mirror
/// of the main project's WasperKpi (Components/Shared/Kpi/WASPer_KpiTypes.cs)
/// -- trimmed to the fields a viewer needs to display a KPI generically,
/// without pulling in that class's Grasshopper/RhinoCommon-coupled fields
/// (SourceInstanceId, SourceComponentId, GH_Component binding, etc.), which
/// XR.Core cannot reference under the Phase 0 BCL-only freeze. Gc07 maps
/// WasperPathKpiExtractor.Extract(path)'s WasperKpiSet.Items into a list of
/// these at export time; both the binary (.wasperxr 0.2.0) and JSON (0.1.0)
/// import paths populate WASPerPrintJob.Kpis from whatever was carried.
/// </summary>
public sealed record PrintJobKpi(
    string Key,
    string Label,
    string Group,
    string Unit,
    double? Value,
    string? TextValue);
