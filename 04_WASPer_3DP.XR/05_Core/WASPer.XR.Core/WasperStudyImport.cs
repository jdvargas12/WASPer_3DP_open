using System.Text.Json;

namespace WASPer.XR.Core;

/// <summary>
/// Converts an existing study.json (written by WasperStudyStorage.Save in
/// the main project, Components/Shared/Studies/WASPer_StudyTypes.cs, via
/// Newtonsoft.Json with default PascalCase property names -- grounded
/// directly against a real exported study.json, not guessed) into a
/// StudySnapshot. Mirrors WasperXrPackageImport's role for .wasperxr: the
/// bridge between an existing manufacturing-side file and the
/// platform-independent model the browser Dashboard consumes. Private DTOs
/// below match study.json's on-disk shape field for field; StudySnapshot
/// itself only carries what a dashboard chart actually needs (GcodeFiles/
/// SnapshotFiles/Warnings/Report/Snapshot settings are intentionally not
/// carried over -- out of scope for charting).
/// </summary>
public static class WasperStudyImport
{
    private static readonly JsonSerializerOptions DtoOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static StudySnapshot FromFile(string path)
    {
        string json = File.ReadAllText(path);
        return FromJson(json);
    }

    public static StudySnapshot FromJson(string json)
    {
        StudyFileDto file = JsonSerializer.Deserialize<StudyFileDto>(json, DtoOptions)
            ?? throw new FormatException("Could not parse study.json.");

        List<StudyParameter> parameters = (file.Parameters ?? new List<StudyParameterDto>())
            .Select(p => new StudyParameter(p.Name, p.Minimum, p.Maximum, p.Samples))
            .ToList();

        List<StudyIteration> iterations = (file.Iterations ?? new List<StudyIterationDto>())
            .Select(ConvertIteration)
            .ToList();

        StudyDashboardDefaults dashboard = ConvertDashboard(file.Dashboard);

        return new StudySnapshot(
            StudyId: file.StudyId ?? string.Empty,
            RunName: file.RunName ?? string.Empty,
            Parameters: parameters,
            Iterations: iterations,
            Dashboard: dashboard);
    }

    private static StudyIteration ConvertIteration(StudyIterationDto dto) => new(
        Index: dto.Index,
        SampleName: dto.SampleName ?? string.Empty,
        Status: dto.Status ?? string.Empty,
        CapturedUtc: dto.CapturedUtc,
        Parameters: dto.Parameters ?? new Dictionary<string, double>(),
        Kpis: (dto.Kpis ?? new List<StudyKpiDto>())
            .Select(k => new PrintJobKpi(k.Key, k.Label, k.Group, k.Unit, k.Value, k.TextValue))
            .ToList());

    private static StudyDashboardDefaults ConvertDashboard(StudyDashboardDto? dto) => new(
        HistoryKpi: ConvertRef(dto?.HistoryKpi),
        ScatterX: ConvertRef(dto?.ScatterX),
        ScatterY: ConvertRef(dto?.ScatterY),
        ScatterColor: ConvertRef(dto?.ScatterColor),
        HistogramVariable: ConvertRef(dto?.HistogramVariable),
        ScatterStyle: dto?.ScatterStyle ?? "Markers",
        HistogramMode: dto?.HistogramMode ?? "Bars",
        HistogramBins: dto?.HistogramBins ?? 11,
        HistogramBandwidthPercent: dto?.HistogramBandwidthPercent ?? 100);

    private static StudyVariableRef? ConvertRef(StudyVariableRefDto? dto) =>
        dto == null || string.IsNullOrEmpty(dto.Key) ? null : new StudyVariableRef(dto.Key, dto.IsInput);

    private sealed record StudyFileDto(
        string? StudyId,
        string? RunName,
        List<StudyParameterDto>? Parameters,
        List<StudyIterationDto>? Iterations,
        StudyDashboardDto? Dashboard);

    private sealed record StudyParameterDto(
        string Name,
        double Minimum,
        double Maximum,
        int Samples);

    private sealed record StudyIterationDto(
        int Index,
        string? SampleName,
        DateTimeOffset CapturedUtc,
        string? Status,
        Dictionary<string, double>? Parameters,
        List<StudyKpiDto>? Kpis);

    private sealed record StudyKpiDto(
        string Key,
        string Label,
        string Group,
        string Unit,
        double? Value,
        string? TextValue);

    private sealed record StudyDashboardDto(
        StudyVariableRefDto? HistoryKpi,
        StudyVariableRefDto? ScatterX,
        StudyVariableRefDto? ScatterY,
        StudyVariableRefDto? ScatterColor,
        StudyVariableRefDto? HistogramVariable,
        string? ScatterStyle,
        string? HistogramMode,
        int HistogramBins,
        int HistogramBandwidthPercent);

    private sealed record StudyVariableRefDto(string Key, bool IsInput);
}
