// wsp_Gc07_Export XR Package.cs
// WASPer_3DP - Subcategory: 5.0_Gcode
// Exports a transport-neutral fabrication plan for desktop and OpenXR viewers.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP;
using WASPer_3DP.Components._1_2_Studies;

namespace WASPer_3DP.Components._5_0_Gcode
{
    public sealed class wsp_Gc07_Export_XR_Package : GH_Component
    {
        internal const string SchemaVersion = WasperXrBinaryPackage.SchemaVersion;
        private readonly string _versionTag;
        private bool _parameterUpdateScheduled;

        public wsp_Gc07_Export_XR_Package()
            : base(
                "wsp_Gc07_Export XR Package",
                "Export XR",
                "Passes display geometry through for live viewers and exports a complete, " +
                "Gc03-enriched WASPer Print Path " +
                "to one compact, versioned .wasperxr binary package for vvvv/VL.Stride desktop or " +
                "OpenXR visualization. The package includes path roles, process and " +
                "bead dimensions, Z orientations, and print/travel/Z-hop timing. Heavy display " +
                "geometry is intentionally excluded from JSON and should be sent through " +
                "VLink/VL.Rhino. Writes " +
                "are atomic so a file watcher cannot read a partial package.\n\n" +
                "Please use Pp01 to construct the path and Gc03 to add its motion plan " +
                "before exporting.",
                WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("F3EDC826-A989-4D19-A52B-57099E154CE6");

        // Retained as a hidden compatibility shell because Sm01 owns the current XR
        // workflow while reusing this component's package-building engine.
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon => WasperXrHeadsetIcon.Bitmap;

        protected override void BeforeSolveInstance()
        {
            base.BeforeSolveInstance();
            bool linked = IsLinkedToStudyManager();
            bool hasFolder = InputIndex("folder") >= 0;
            bool hasExport = InputIndex("export") >= 0;
            if ((linked && (hasFolder || hasExport)) ||
                (!linked && (!hasFolder || !hasExport)))
                ScheduleStudyManagerParameterMode(linked);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            int geometryIndex = p.AddGeometryParameter(
                "display_geometry",
                "geo",
                "Optional display geometry passed through for VLink/VL.Rhino. It is not " +
                "serialized into the XR JSON package.",
                GH_ParamAccess.list);
            p[geometryIndex].Optional = true;

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Complete Gc03-enriched WASPer Print Path with a resolved motion plan.",
                GH_ParamAccess.item);

            p.AddNumberParameter(
                "simulation_parameter",
                "sim_par",
                "Normalized deposited-path parameter from 0 to 1. Connect Gc05 sim_path " +
                "for live Grasshopper playback, or set manually to split the complete path " +
                "into printed and pending curves.",
                GH_ParamAccess.item,
                1.0);

            p.AddTextParameter(
                "folder",
                "folder",
                "Existing destination folder for the compact .wasperxr package.",
                GH_ParamAccess.item);
            p[3].Optional = true;

            p.AddBooleanParameter(
                "export",
                "export",
                "Set true to serialize and atomically write the XR package.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGeometryParameter(
                "display_geometry",
                "geo",
                "Display geometry passed through unchanged for VLinkSend or another live viewer.",
                GH_ParamAccess.list);

            p.AddCurveParameter(
                "printed_paths",
                "printed",
                "Path curves deposited at sim_par. Partial branches end at the current split.",
                GH_ParamAccess.list);

            p.AddCurveParameter(
                "pending_paths",
                "pending",
                "Path curves not yet deposited at sim_par. Partial branches begin at the split.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "file",
                "file",
                "Written compact .wasperxr file path.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "manifest",
                "manifest",
                "Small human-readable manifest describing the binary XR package.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Export status, geometry counts, duration, units, and diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            var geometry = new List<GeometryBase>();
            da.GetDataList(0, geometry);
            List<GeometryBase> validGeometry = geometry
                .Where(item => item != null && item.IsValid)
                .ToList();
            da.SetDataList(0, validGeometry);

            if (!WasperGcodeTreeUtil.TryGetPrintPath(da, 1, out WasperPrintPath path) ||
                path == null ||
                !path.HasPoints)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path must contain at least one valid path branch.");
                return;
            }
            if (path.IsPartial)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "wsp_path is marked partial. Gc07 can export the supplied deposited prefix, " +
                    "but it cannot reconstruct pending paths that were removed upstream. " +
                    "Connect the complete Gc03-enriched path and drive sim_par separately to " +
                    "visualize both printed and pending geometry.");
            }

            double simulationParameter = 1.0;
            string folder = null;
            bool export = false;

            da.GetData(2, ref simulationParameter);
            int folderIndex = InputIndex("folder");
            int exportIndex = InputIndex("export");
            if (folderIndex >= 0)
                da.GetData(folderIndex, ref folder);
            if (exportIndex >= 0)
                da.GetData(exportIndex, ref export);
            if (IsLinkedToStudyManager())
            {
                folder = null;
                export = false;
            }
            double clampedSimulationParameter = Math.Max(0.0, Math.Min(1.0, simulationParameter));
            if (Math.Abs(clampedSimulationParameter - simulationParameter) > 1e-12)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "sim_par was clamped to the 0-to-1 interval.");
            }

            SplitPathCurves(
                path,
                clampedSimulationParameter,
                out List<Curve> printedPaths,
                out List<Curve> pendingPaths);
            da.SetDataList(1, printedPaths);
            da.SetDataList(2, pendingPaths);

            if (!export)
            {
                da.SetData(5, BuildReadySummary(
                    path,
                    validGeometry.Count,
                    clampedSimulationParameter,
                    printedPaths.Count,
                    pendingPaths.Count));
                return;
            }

            string jobId = ResolveAutomaticJobId(folder);
            int revision = NextAutomaticRevision(folder, jobId);

            if (!TryExportPackage(
                path,
                clampedSimulationParameter,
                folder,
                jobId,
                revision,
                _versionTag,
                out string finalPath,
                out string json,
                out string summary,
                out string error))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            da.SetData(3, finalPath);
            da.SetData(4, json);
            da.SetData(5, summary);
        }

        internal void SetStudyManagerLinkedMode(bool linked)
        {
            bool changed = false;
            if (linked)
            {
                changed |= RemoveInput("export");
                changed |= RemoveInput("folder");
            }
            else
            {
                if (InputIndex("folder") < 0)
                {
                    Params.RegisterInputParam(CreateFolderInput(), Params.Input.Count);
                    changed = true;
                }
                if (InputIndex("export") < 0)
                {
                    Params.RegisterInputParam(CreateExportInput(), Params.Input.Count);
                    changed = true;
                }
            }

            if (!changed)
                return;
            Params.OnParametersChanged();
            Attributes?.ExpireLayout();
            OnObjectChanged(GH_ObjectEventType.Layout);
        }

        private void ScheduleStudyManagerParameterMode(bool linked)
        {
            if (_parameterUpdateScheduled)
                return;
            GH_Document document = OnPingDocument();
            if (document == null)
                return;
            _parameterUpdateScheduled = true;
            document.ScheduleSolution(1, scheduledDocument =>
            {
                _parameterUpdateScheduled = false;
                SetStudyManagerLinkedMode(IsLinkedToStudyManager());
                ExpireSolution(false);
                Instances.RedrawCanvas();
            });
        }

        private bool IsLinkedToStudyManager()
        {
            if (Params.Input.Count < 2)
                return false;
            GH_Document document = OnPingDocument();
            if (document == null)
                return false;
            IList<IGH_Param> sources = Params.Input[1].Sources;
            return document.Objects
                .OfType<wsp_Sm01_WASPer_Study_Manager>()
                .SelectMany(component => component.Params.Output)
                .Any(output => sources.Any(source => ReferenceEquals(source, output)));
        }

        private int InputIndex(string name) => Params.Input.FindIndex(parameter =>
            string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));

        private bool RemoveInput(string name)
        {
            int index = InputIndex(name);
            return index >= 0 && Params.UnregisterInputParameter(Params.Input[index], true);
        }

        private static Param_String CreateFolderInput() => new Param_String
        {
            Name = "folder",
            NickName = "folder",
            Description = "Existing destination folder for the compact .wasperxr package.",
            Access = GH_ParamAccess.item,
            Optional = true
        };

        private static Param_Boolean CreateExportInput()
        {
            var parameter = new Param_Boolean
            {
                Name = "export",
                NickName = "export",
                Description = "Set true to serialize and atomically write the XR package.",
                Access = GH_ParamAccess.item
            };
            parameter.PersistentData.Append(new GH_Boolean(false));
            return parameter;
        }

        internal static bool TryExportPackage(
            WasperPrintPath path,
            double simulationParameter,
            string folder,
            string jobId,
            int revision,
            string pluginVersion,
            out string finalPath,
            out string json,
            out string summary,
            out string error,
            WasperKpiSet globalKpiSet = null,
            bool disablePlayback = false,
            // Distinct from the `simulationParameter` parameter above (which only affects this
            // method's own printed/pending curve-split outputs) -- this is Sm05's sim_par value,
            // only meaningful to the browser when disablePlayback is true. Named separately to
            // avoid confusing the two.
            double externalSimulationParameter = 1.0,
            WasperXrScenePack scenePack = null)
        {
            finalPath = string.Empty;
            json = string.Empty;
            summary = string.Empty;
            error = string.Empty;

            if (path == null || !path.HasPoints)
            {
                error = "wsp_path must contain at least one valid path branch.";
                return false;
            }
            if (!path.HasMotionPlan)
            {
                error = "wsp_path has no motion plan. Pass the Pp01 path through Gc03 before " +
                    "exporting so the XR viewer can distinguish deposited, active, and " +
                    "remaining movements by time.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "A Process Viewer export folder is required.";
                return false;
            }
            if (revision < 0)
            {
                error = "revision must be zero or greater.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(folder);
                string safeJobId = SanitizeJobId(jobId);
                if (string.IsNullOrWhiteSpace(safeJobId))
                    safeJobId = "wasper-job";

                UnitSystem units = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Millimeters;
                double metresPerUnit = RhinoMath.UnitScale(units, UnitSystem.Meters);
                if (!double.IsFinite(metresPerUnit) || metresPerUnit <= 0.0)
                    metresPerUnit = 0.001;

                // KPI set carried in the binary package. If the caller (Sm01,
                // wired to Pr01/Pr03/Ch04/Ch07/In08-10/Ht01 etc. via its own
                // kpi_sets input) supplied its merged, user-filtered set --
                // Fabrication plus whatever other groups (Infill, material,
                // thermal...) are wired and enabled -- use exactly that, via
                // EnabledItems so items the user unticked in Sm01's KPI tab
                // stay excluded. A bare Gc07 export with no Sm01 in play has
                // no such set, so it falls back to computing the Fabrication
                // group alone from the path, same as before.
                IEnumerable<WasperKpi> exportKpis = globalKpiSet != null && globalKpiSet.Items.Count > 0
                    ? globalKpiSet.EnabledItems
                    : WasperPathKpiExtractor.Extract(path).Items;
                List<WasperKpi> exportKpiList = exportKpis.ToList();

                finalPath = Path.Combine(folder, safeJobId + WasperXrBinaryPackage.Extension);
                WasperXrBinaryPackage.WriteAtomic(
                    finalPath,
                    path,
                    safeJobId,
                    revision,
                    pluginVersion,
                    units,
                    metresPerUnit,
                    exportKpiList,
                    disablePlayback,
                    externalSimulationParameter,
                    scenePack);
                long packageBytes = new FileInfo(finalPath).Length;
                json = JsonConvert.SerializeObject(new
                {
                    schemaVersion = SchemaVersion,
                    type = "wasper.xr.printPlan",
                    encoding = "wasper-binary-v1+gzip",
                    jobId = safeJobId,
                    revision,
                    pathBranches = path.Points.BranchCount,
                    pathPoints = path.PointCount,
                    motions = path.MotionPlan.Count,
                    durationSeconds = path.MotionPlan.DurationMinutes * 60.0,
                    kpiCount = exportKpiList.Count,
                    bytes = packageBytes
                }, Formatting.Indented);
                summary =
                    $"Exported compact XR schema {SchemaVersion}: live geometry excluded, " +
                    $"{path.Points.BranchCount} path branch(es), " +
                    $"{path.PointCount} path point(s), " +
                    $"{path.MotionPlan.Count} motion(s), " +
                    $"{path.MotionPlan.DurationMinutes * 60.0:0.###} s, " +
                    $"{exportKpiList.Count} KPI(s), " +
                    $"{packageBytes / (1024.0 * 1024.0):0.##} MiB. " +
                    $"Units: {units}; metres/unit: " +
                    $"{metresPerUnit.ToString("G17", CultureInfo.InvariantCulture)}. " +
                    $"Saved: {finalPath}";
                return true;
            }
            catch (Exception exception)
            {
                error = "XR export failed: " + exception.Message;
                return false;
            }
        }

        // Live-link support (M5, added 2026-08-19): builds the exact same binary payload
        // TryExportPackage writes to disk, but returns it in memory for WasperLiveViewerClient
        // to push over a WebSocket instead. No folder/revision-on-disk bookkeeping here --
        // revision is caller-supplied (0 for "live", meaning no persisted file exists for this
        // frame) since there is nothing on disk to increment against.
        internal static bool TryBuildLivePackageBytes(
            WasperPrintPath path,
            string jobId,
            int revision,
            string pluginVersion,
            WasperKpiSet globalKpiSet,
            out byte[] bytes,
            out string error,
            bool disablePlayback = false,
            double externalSimulationParameter = 1.0,
            WasperXrScenePack scenePack = null)
        {
            bytes = null;
            error = string.Empty;

            if (path == null || !path.HasPoints)
            {
                error = "wsp_path must contain at least one valid path branch.";
                return false;
            }
            if (!path.HasMotionPlan)
            {
                error = "wsp_path has no motion plan yet -- connect it through Gc03 before " +
                    "the live viewer has anything to show.";
                return false;
            }

            try
            {
                string safeJobId = SanitizeJobId(jobId);
                if (string.IsNullOrWhiteSpace(safeJobId))
                    safeJobId = "wasper-job";

                UnitSystem units = RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Millimeters;
                double metresPerUnit = RhinoMath.UnitScale(units, UnitSystem.Meters);
                if (!double.IsFinite(metresPerUnit) || metresPerUnit <= 0.0)
                    metresPerUnit = 0.001;

                // Same EnabledItems-or-Fabrication-fallback rule as TryExportPackage, above --
                // a live-linked Sm01 pushes its full merged/filtered KPI set, a bare Gc07 (no
                // Study Manager in play) falls back to Fabrication alone from the path.
                IEnumerable<WasperKpi> liveKpis = globalKpiSet != null && globalKpiSet.Items.Count > 0
                    ? globalKpiSet.EnabledItems
                    : WasperPathKpiExtractor.Extract(path).Items;

                bytes = WasperXrBinaryPackage.WriteToBytes(
                    path,
                    safeJobId,
                    Math.Max(0, revision),
                    pluginVersion,
                    units,
                    metresPerUnit,
                    liveKpis.ToList(),
                    disablePlayback,
                    externalSimulationParameter,
                    scenePack,
                    includeContextNormals: false);
                return true;
            }
            catch (Exception exception)
            {
                error = "Live package build failed: " + exception.Message;
                return false;
            }
        }

        private static XrEnvelope BuildPackage(
            WasperPrintPath path,
            double simulationParameter,
            string jobId,
            int revision,
            UnitSystem units,
            double metresPerUnit,
            string pluginVersion)
        {
            var exportedMeshes = new List<XrMesh>();
            BoundingBox bounds = BoundingBox.Empty;

            List<XrPathBranch> paths = ExportPaths(path, ref bounds);
            List<XrMotion> motions = ExportMotions(path);
            double durationSeconds = motions.Count == 0
                ? 0.0
                : motions[motions.Count - 1].EndTimeSeconds;

            var summary = new XrSummary
            {
                MeshCount = exportedMeshes.Count,
                MeshVertexCount = 0,
                MeshFaceCount = 0,
                PathBranchCount = paths.Count,
                PathPointCount = paths.Sum(branch => branch.Positions.Count),
                MotionCount = motions.Count,
                DurationSeconds = durationSeconds,
                LayerCount = CountLogicalLayers(path.Points),
                Bounds = bounds.IsValid ? ExportBounds(bounds) : null,
                Kpis = ExportKpis(path)
            };

            return new XrEnvelope
            {
                SchemaVersion = SchemaVersion,
                Type = "wasper.xr.printPlan",
                JobId = jobId,
                Revision = revision,
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                PluginVersion = string.IsNullOrWhiteSpace(pluginVersion)
                    ? "v1.0.x"
                    : pluginVersion,
                Coordinates = new XrCoordinates
                {
                    Frame = "WASPer",
                    Units = units.ToString(),
                    MetresPerUnit = metresPerUnit,
                    Handedness = "right",
                    UpAxis = "+Z"
                },
                Payload = new XrPayload
                {
                    Meshes = exportedMeshes,
                    Paths = paths,
                    Motions = motions,
                    Simulation = ExportSimulation(path, simulationParameter),
                    Summary = summary
                }
            };
        }

        private static List<XrPathBranch> ExportPaths(
            WasperPrintPath path,
            ref BoundingBox bounds)
        {
            var output = new List<XrPathBranch>();
            DataTree<Plane> planes = path.PtPlanes;

            for (int branchIndex = 0; branchIndex < planes.BranchCount; branchIndex++)
            {
                GH_Path branchPath = planes.Paths[branchIndex];
                IList<Plane> branch = planes.Branches[branchIndex];
                if (branch == null || branch.Count == 0)
                    continue;

                int roleCode = ResolveScalar(path.PathRoles, branchPath, 0);
                int strokeId = ResolveScalar(path.StrokeIds, branchPath, -1);
                var positions = new List<double[]>(branch.Count);
                var xAxes = new List<double[]>(branch.Count);
                var yAxes = new List<double[]>(branch.Count);
                var zAxes = new List<double[]>(branch.Count);

                for (int pointIndex = 0; pointIndex < branch.Count; pointIndex++)
                {
                    Plane plane = branch[pointIndex];
                    positions.Add(Point(plane.Origin));
                    xAxes.Add(Vector(plane.XAxis));
                    yAxes.Add(Vector(plane.YAxis));
                    zAxes.Add(Vector(plane.ZAxis));
                    bounds.Union(plane.Origin);
                }

                output.Add(new XrPathBranch
                {
                    BranchIndex = branchIndex,
                    BranchPath = branchPath.ToString(),
                    LayerIndex = branchPath.Indices.Length > 0 ? branchPath.Indices[0] : 0,
                    Role = roleCode,
                    RoleName = WasperPathRoleMetadata.RoleName(
                        Enum.IsDefined(typeof(WasperPathRole), roleCode)
                            ? (WasperPathRole)roleCode
                            : WasperPathRole.Undefined),
                    StrokeId = strokeId,
                    Closed = branch.Count > 2 &&
                        branch[0].Origin.DistanceToSquared(branch[branch.Count - 1].Origin) <=
                        RhinoMath.ZeroTolerance * RhinoMath.ZeroTolerance,
                    Positions = positions,
                    XAxes = xAxes,
                    YAxes = yAxes,
                    ZAxes = zAxes,
                    Values = ExportBranchValues(path, branchPath, branch.Count)
                });
            }

            return output;
        }

        private static Dictionary<string, object> ExportBranchValues(
            WasperPrintPath path,
            GH_Path branchPath,
            int pointCount)
        {
            var values = new Dictionary<string, object>();
            Add(values, "flow", ResolveValues(path.Flows, branchPath, pointCount));
            Add(values, "printSpeed", ResolveValues(path.PrintSpeed, branchPath, pointCount));
            Add(values, "layerHeight", ResolveValues(path.LayerH, branchPath, pointCount));
            Add(values, "layerWidth", ResolveValues(path.LayerW, branchPath, pointCount));
            Add(values, "layerWidthFlowAdjusted", ResolveValues(path.LayerWf, branchPath, pointCount));
            Add(values, "printabilityLocal", ResolveValues(path.PrintLoc, branchPath, pointCount));
            Add(values, "printabilityGlobal", ResolveValues(path.PrintGlob, branchPath, pointCount));
            Add(values, "supportAngle", ResolveValues(path.Angles, branchPath, pointCount));
            Add(values, "contactWidth", ResolveValues(path.ContactWidths, branchPath, pointCount));
            Add(values, "riskMaterial", ResolveValues(path.RiskMaterial, branchPath, pointCount));
            Add(values, "riskCombined", ResolveValues(path.RiskComb, branchPath, pointCount));
            Add(values, "deflectionRatio", ResolveValues(path.DRatio, branchPath, pointCount));
            Add(values, "loadedDeflectionRatio", ResolveValues(path.DLoaded, branchPath, pointCount));
            Add(values, "bendingRatio", ResolveValues(path.BendRatio, branchPath, pointCount));
            Add(values, "spanLength", ResolveValues(path.SpanLen, branchPath, pointCount));
            Add(values, "collapsed", ResolveValues(path.Collapsed, branchPath, pointCount));
            Add(values, "failureFlags", ResolveValues(path.FailureFlags, branchPath, pointCount));
            return values;
        }

        private static List<XrMotion> ExportMotions(WasperPrintPath path)
        {
            var output = new List<XrMotion>(path.MotionPlan.Count);
            double time = 0.0;

            for (int index = 0; index < path.MotionPlan.Count; index++)
            {
                WasperMotion motion = path.MotionPlan.Motions[index];
                double start = time;
                time += motion.DurationMinutes * 60.0;

                GH_Path branchPath = motion.BranchIndex >= 0 &&
                    motion.BranchIndex < path.Points.Paths.Count
                    ? path.Points.Paths[motion.BranchIndex]
                    : null;
                int roleCode = branchPath == null
                    ? 0
                    : ResolveScalar(path.PathRoles, branchPath, 0);

                output.Add(new XrMotion
                {
                    Index = index,
                    Type = motion.Type.ToString().ToLowerInvariant(),
                    LayerIndex = motion.LayerIndex,
                    BranchIndex = motion.BranchIndex,
                    BranchPath = branchPath?.ToString(),
                    PointIndex = motion.PointIndex,
                    Role = roleCode,
                    RoleName = WasperPathRoleMetadata.RoleName(
                        Enum.IsDefined(typeof(WasperPathRole), roleCode)
                            ? (WasperPathRole)roleCode
                            : WasperPathRole.Undefined),
                    From = Point(motion.From),
                    To = Point(motion.To),
                    FeedrateMmPerMinute = motion.Feedrate,
                    LengthModelUnits = motion.Length,
                    StartTimeSeconds = start,
                    EndTimeSeconds = time
                });
            }

            return output;
        }

        private static Dictionary<string, object> ExportKpis(WasperPrintPath path)
        {
            var kpis = new Dictionary<string, object>();
            Add(kpis, "unitsCode", path.KpiUnits);
            Add(kpis, "timeMinutes", path.KpiTimeMin);
            Add(kpis, "pathLength", path.KpiPathLength);
            Add(kpis, "volume", path.KpiVolume);
            Add(kpis, "massKg", path.KpiMassKg);
            Add(kpis, "layers", path.KpiLayers);
            Add(kpis, "nozzleDiameterMm", path.NozzleDiam);
            Add(kpis, "travelSpeedMmPerMinute", path.TravelSpeed);
            Add(kpis, "zHopModelUnits", path.ZHop);
            Add(kpis, "zHopSpeedMmPerMinute", path.ZHopSpeed);
            return kpis;
        }

        private static void SplitPathCurves(
            WasperPrintPath path,
            double simulationParameter,
            out List<Curve> printed,
            out List<Curve> pending)
        {
            printed = new List<Curve>();
            pending = new List<Curve>();
            int remaining = SimulationCompletedPointCount(path, simulationParameter);
            DataTree<Plane> planes = path.PtPlanes;

            for (int branchIndex = 0; branchIndex < planes.BranchCount; branchIndex++)
            {
                IList<Plane> branch = planes.Branches[branchIndex];
                int count = branch?.Count ?? 0;
                if (count == 0)
                    continue;

                int take = Math.Min(count, remaining);
                remaining -= take;
                if (take >= 2)
                {
                    printed.Add(new PolylineCurve(
                        branch.Take(take).Select(plane => plane.Origin)));
                }

                if (take < count)
                {
                    int pendingStart = Math.Max(0, take - 1);
                    List<Point3d> pendingPoints = branch
                        .Skip(pendingStart)
                        .Select(plane => plane.Origin)
                        .ToList();
                    if (pendingPoints.Count >= 2)
                        pending.Add(new PolylineCurve(pendingPoints));
                }
            }
        }

        private static XrSimulationState ExportSimulation(
            WasperPrintPath path,
            double simulationParameter)
        {
            int completed = SimulationCompletedPointCount(path, simulationParameter);
            int remaining = completed;
            var printed = new List<XrPathFragment>();
            var pending = new List<XrPathFragment>();
            DataTree<Plane> planes = path.PtPlanes;

            for (int branchIndex = 0; branchIndex < planes.BranchCount; branchIndex++)
            {
                GH_Path branchPath = planes.Paths[branchIndex];
                IList<Plane> branch = planes.Branches[branchIndex];
                int count = branch?.Count ?? 0;
                if (count == 0)
                    continue;

                int take = Math.Min(count, remaining);
                remaining -= take;
                if (take >= 2)
                {
                    printed.Add(ExportPathFragment(
                        path,
                        branchIndex,
                        branchPath,
                        branch.Take(take).ToList(),
                        take == count));
                }
                if (take < count)
                {
                    int pendingStart = Math.Max(0, take - 1);
                    IList<Plane> pendingPlanes = branch.Skip(pendingStart).ToList();
                    if (pendingPlanes.Count >= 2)
                    {
                        pending.Add(ExportPathFragment(
                            path,
                            branchIndex,
                            branchPath,
                            pendingPlanes,
                            take == 0));
                    }
                }
            }

            return new XrSimulationState
            {
                Parameter = Math.Max(0.0, Math.Min(1.0, simulationParameter)),
                CompletedPointCount = completed,
                TotalPointCount = path.PointCount,
                SourceIsPartial = path.IsPartial,
                PrintedPaths = printed,
                PendingPaths = pending
            };
        }

        private static XrPathFragment ExportPathFragment(
            WasperPrintPath path,
            int branchIndex,
            GH_Path branchPath,
            IList<Plane> planes,
            bool completeBranch)
        {
            int roleCode = ResolveScalar(path.PathRoles, branchPath, 0);
            return new XrPathFragment
            {
                BranchIndex = branchIndex,
                BranchPath = branchPath.ToString(),
                Role = roleCode,
                RoleName = WasperPathRoleMetadata.RoleName(
                    Enum.IsDefined(typeof(WasperPathRole), roleCode)
                        ? (WasperPathRole)roleCode
                        : WasperPathRole.Undefined),
                CompleteBranch = completeBranch,
                Positions = planes.Select(plane => Point(plane.Origin)).ToList()
            };
        }

        private static int SimulationCompletedPointCount(
            WasperPrintPath path,
            double simulationParameter)
        {
            double clamped = Math.Max(0.0, Math.Min(1.0, simulationParameter));
            return Math.Max(0, Math.Min(
                path.PointCount,
                (int)Math.Round(
                    clamped * path.PointCount,
                    MidpointRounding.AwayFromZero)));
        }

        private static List<double> ResolveValues(
            DataTree<double> tree,
            GH_Path path,
            int targetCount)
        {
            if (tree == null || !tree.PathExists(path))
                return null;

            IList<double> branch = tree.Branch(path);
            return Expand(branch, targetCount);
        }

        private static List<bool> ResolveValues(
            DataTree<bool> tree,
            GH_Path path,
            int targetCount)
        {
            if (tree == null || !tree.PathExists(path))
                return null;

            IList<bool> branch = tree.Branch(path);
            return Expand(branch, targetCount);
        }

        private static List<int> ResolveValues(
            DataTree<int> tree,
            GH_Path path,
            int targetCount)
        {
            if (tree == null || !tree.PathExists(path))
                return null;

            IList<int> branch = tree.Branch(path);
            return Expand(branch, targetCount);
        }

        private static List<T> Expand<T>(IList<T> source, int targetCount)
        {
            if (source == null || source.Count == 0)
                return null;

            if (source.Count == targetCount)
                return new List<T>(source);

            var output = new List<T>(targetCount);
            for (int i = 0; i < targetCount; i++)
                output.Add(source[source.Count == 1 ? 0 : Math.Min(i, source.Count - 1)]);
            return output;
        }

        private static int ResolveScalar(DataTree<int> tree, GH_Path path, int fallback)
        {
            if (tree == null || path == null || !tree.PathExists(path))
                return fallback;

            IList<int> branch = tree.Branch(path);
            return branch == null || branch.Count == 0 ? fallback : branch[0];
        }

        private static void Add(
            IDictionary<string, object> values,
            string name,
            object value)
        {
            if (value != null)
                values[name] = value;
        }

        private static string BuildReadySummary(
            WasperPrintPath path,
            int liveGeometryCount,
            double simulationParameter,
            int printedCurveCount,
            int pendingCurveCount)
        {
            string timing = path.HasMotionPlan
                ? $"{path.MotionPlan.Count} motions, {path.MotionPlan.DurationMinutes * 60.0:0.###} s"
                : "no motion plan; pass the path through Gc03 before export";
            return $"Ready: {liveGeometryCount} live geometry object(s), " +
                $"{path.Points.BranchCount} path branch(es), {path.PointCount} point(s), {timing}. " +
                $"Simulation {simulationParameter:P0}: {printedCurveCount} printed curve(s), " +
                $"{pendingCurveCount} pending curve(s).";
        }

        private string ResolveAutomaticJobId(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var directory = new DirectoryInfo(folder);
                if (string.Equals(directory.Name, "XR", StringComparison.OrdinalIgnoreCase) &&
                    directory.Parent != null)
                {
                    return SanitizeJobId(directory.Parent.Name);
                }
            }

            string documentPath = OnPingDocument()?.FilePath;
            if (!string.IsNullOrWhiteSpace(documentPath))
                return SanitizeJobId(Path.GetFileNameWithoutExtension(documentPath));
            return "wasper-xr";
        }

        private static int NextAutomaticRevision(string folder, string jobId)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return 1;
            string path = Path.Combine(folder, SanitizeJobId(jobId) + WasperXrBinaryPackage.Extension);
            if (!File.Exists(path))
                return 1;
            return WasperXrBinaryPackage.ReadRevision(path) + 1;
        }

        private static int CountLogicalLayers(DataTree<Point3d> points)
        {
            var layers = new HashSet<int>();
            for (int i = 0; i < points.Paths.Count; i++)
            {
                GH_Path path = points.Paths[i];
                layers.Add(path.Indices.Length > 0 ? path.Indices[0] : 0);
            }
            return layers.Count;
        }

        private static XrBounds ExportBounds(BoundingBox bounds)
        {
            return new XrBounds
            {
                Min = Point(bounds.Min),
                Max = Point(bounds.Max)
            };
        }

        private static double[] Point(Point3d point) =>
            new[] { point.X, point.Y, point.Z };

        private static double[] Vector(Vector3d vector) =>
            new[] { vector.X, vector.Y, vector.Z };

        internal static string SanitizeJobId(string jobId)
        {
            string value = string.IsNullOrWhiteSpace(jobId)
                ? "wasper-job"
                : jobId.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
                builder.Append(invalid.Contains(character) ? '_' : character);
            return builder.ToString();
        }

        private static void WriteAtomic(string finalPath, string contents)
        {
            string temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
                File.Move(temporaryPath, finalPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private sealed class XrEnvelope
        {
            public string SchemaVersion { get; set; }
            public string Type { get; set; }
            public string JobId { get; set; }
            public int Revision { get; set; }
            public string TimestampUtc { get; set; }
            public string PluginVersion { get; set; }
            public XrCoordinates Coordinates { get; set; }
            public XrPayload Payload { get; set; }
        }

        private sealed class XrCoordinates
        {
            public string Frame { get; set; }
            public string Units { get; set; }
            public double MetresPerUnit { get; set; }
            public string Handedness { get; set; }
            public string UpAxis { get; set; }
        }

        private sealed class XrPayload
        {
            public List<XrMesh> Meshes { get; set; }
            public List<XrPathBranch> Paths { get; set; }
            public List<XrMotion> Motions { get; set; }
            public XrSimulationState Simulation { get; set; }
            public XrSummary Summary { get; set; }
        }

        private sealed class XrSimulationState
        {
            public double Parameter { get; set; }
            public int CompletedPointCount { get; set; }
            public int TotalPointCount { get; set; }
            public bool SourceIsPartial { get; set; }
            public List<XrPathFragment> PrintedPaths { get; set; }
            public List<XrPathFragment> PendingPaths { get; set; }
        }

        private sealed class XrPathFragment
        {
            public int BranchIndex { get; set; }
            public string BranchPath { get; set; }
            public int Role { get; set; }
            public string RoleName { get; set; }
            public bool CompleteBranch { get; set; }
            public List<double[]> Positions { get; set; }
        }

        private sealed class XrMesh
        {
            public string Id { get; set; }
            public List<double[]> Vertices { get; set; }
            public List<double[]> Normals { get; set; }
            public List<int[]> Faces { get; set; }
            public List<int[]> ColorsRgba { get; set; }
        }

        private sealed class XrPathBranch
        {
            public int BranchIndex { get; set; }
            public string BranchPath { get; set; }
            public int LayerIndex { get; set; }
            public int Role { get; set; }
            public string RoleName { get; set; }
            public int StrokeId { get; set; }
            public bool Closed { get; set; }
            public List<double[]> Positions { get; set; }
            public List<double[]> XAxes { get; set; }
            public List<double[]> YAxes { get; set; }
            public List<double[]> ZAxes { get; set; }
            public Dictionary<string, object> Values { get; set; }
        }

        private sealed class XrMotion
        {
            public int Index { get; set; }
            public string Type { get; set; }
            public int LayerIndex { get; set; }
            public int BranchIndex { get; set; }
            public string BranchPath { get; set; }
            public int PointIndex { get; set; }
            public int Role { get; set; }
            public string RoleName { get; set; }
            public double[] From { get; set; }
            public double[] To { get; set; }
            public double FeedrateMmPerMinute { get; set; }
            public double LengthModelUnits { get; set; }
            public double StartTimeSeconds { get; set; }
            public double EndTimeSeconds { get; set; }
        }

        private sealed class XrSummary
        {
            public int MeshCount { get; set; }
            public int MeshVertexCount { get; set; }
            public int MeshFaceCount { get; set; }
            public int PathBranchCount { get; set; }
            public int PathPointCount { get; set; }
            public int MotionCount { get; set; }
            public int LayerCount { get; set; }
            public double DurationSeconds { get; set; }
            public XrBounds Bounds { get; set; }
            public Dictionary<string, object> Kpis { get; set; }
        }

        private sealed class XrBounds
        {
            public double[] Min { get; set; }
            public double[] Max { get; set; }
        }
    }

    internal static class WasperXrHeadsetIcon
    {
        private static Bitmap _bitmap;

        public static Bitmap Bitmap
        {
            get
            {
                if (_bitmap != null)
                    return _bitmap;

                _bitmap = new Bitmap(24, 24);
                using (Graphics graphics = Graphics.FromImage(_bitmap))
                using (var outline = new Pen(Color.FromArgb(35, 39, 45), 2f))
                using (var visor = new SolidBrush(Color.FromArgb(38, 180, 210)))
                using (var lens = new SolidBrush(Color.White))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    graphics.DrawArc(outline, 4f, 2.5f, 16f, 14f, 200f, 140f);
                    using (GraphicsPath body = RoundedRectangle(3f, 7f, 18f, 11f, 3f))
                    {
                        graphics.FillPath(visor, body);
                        graphics.DrawPath(outline, body);
                    }

                    graphics.FillEllipse(lens, 6.5f, 10.5f, 4f, 3f);
                    graphics.FillEllipse(lens, 13.5f, 10.5f, 4f, 3f);
                    graphics.DrawLine(outline, 3f, 11f, 1.5f, 12.5f);
                    graphics.DrawLine(outline, 21f, 11f, 22.5f, 12.5f);
                    graphics.DrawArc(outline, 9f, 15f, 6f, 5f, 15f, 150f);
                }
                return _bitmap;
            }
        }

        private static GraphicsPath RoundedRectangle(
            float x,
            float y,
            float width,
            float height,
            float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(x, y, diameter, diameter, 180f, 90f);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270f, 90f);
            path.AddArc(
                x + width - diameter,
                y + height - diameter,
                diameter,
                diameter,
                0f,
                90f);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
