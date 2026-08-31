namespace WASPer.XR.Core;

/// <summary>
/// Platform-independent mirror of one WasperStudyIteration
/// (Components/Shared/Studies/WASPer_StudyTypes.cs in the main project) --
/// one Cartesian-study run's captured parameters and KPIs. Kpis reuses
/// PrintJobKpi rather than inventing a second KPI shape; study.json's
/// per-iteration Kpis list is already filtered to what was enabled at
/// capture time (Sm01 stores CloneKpis(_currentSet?.EnabledItems)), so no
/// further enabled-filtering is needed on read.
/// </summary>
public sealed record StudyIteration(
    int Index,
    string SampleName,
    string Status,
    DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<string, double> Parameters,
    IReadOnlyList<PrintJobKpi> Kpis);

/// <summary>One Cartesian-study input parameter's sweep definition.</summary>
public sealed record StudyParameter(
    string Name,
    double Minimum,
    double Maximum,
    int Samples);

/// <summary>
/// Points at one variable a dashboard chart can plot -- either a study input
/// parameter (IsInput true, looked up in StudyIteration.Parameters by Key)
/// or a KPI (IsInput false, looked up in StudyIteration.Kpis by
/// PrintJobKpi.Key), mirroring WasperDashboardVariableRef.
/// </summary>
public sealed record StudyVariableRef(string Key, bool IsInput);

/// <summary>
/// The native Dashboard tab's persisted chart configuration
/// (WasperDashboardSettings in study.json's "Dashboard" block) -- carried
/// over so the browser Dashboard opens showing the same KPI/parameter
/// selections, style, and binning the user already has configured natively,
/// rather than starting from arbitrary defaults every time.
/// </summary>
public sealed record StudyDashboardDefaults(
    StudyVariableRef? HistoryKpi,
    StudyVariableRef? ScatterX,
    StudyVariableRef? ScatterY,
    StudyVariableRef? ScatterColor,
    StudyVariableRef? HistogramVariable,
    string ScatterStyle,
    string HistogramMode,
    int HistogramBins,
    int HistogramBandwidthPercent);

/// <summary>
/// Platform-independent mirror of one WasperStudy (study.json) -- every
/// iteration a Cartesian study captured, plus the swept parameter
/// definitions and the native Dashboard tab's saved chart configuration.
/// </summary>
public sealed record StudySnapshot(
    string StudyId,
    string RunName,
    IReadOnlyList<StudyParameter> Parameters,
    IReadOnlyList<StudyIteration> Iterations,
    StudyDashboardDefaults Dashboard);
