using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using WASPer_3DP;

namespace WASPer_3DP.Components._4_1_Printability
{
    /// <summary>
    /// KPI-only inspector for a WasperPrintPath (wsp_path wire). Pp04 owns the
    /// geometric/process path deconstruction used for visualization; this component
    /// exposes planes only as a reference frame and then dynamically shows the
    /// Pr01/Pr03/Pr04 diagnostic fields that are actually present in the path.
    /// Pure inspector: performs no analysis and never modifies the path.
    /// </summary>
    public sealed class wsp_Pr05_Deconstruct_Path_KPIs : GH_Component
    {
        private const string ShowAllKey = "show_all_outputs";
        private const string ForcedCategoriesKey = "forced_categories";
        private const string OutputLayoutVersionKey = "output_layout_version";
        private const string OutputLayoutKeysKey = "output_layout_keys";
        private const int OutputLayoutVersion = 1;
        private bool _showAllOutputs;
        private readonly HashSet<string> _forcedCategories = new HashSet<string>(StringComparer.Ordinal);

        public wsp_Pr05_Deconstruct_Path_KPIs()
            : base(
                "wsp_Pr05_Deconstruct Path KPIs",
                "DecKPIs",
                "Deconstructs per-point and per-segment KPI / diagnostic information carried by a WASPer Print Path (wsp_path). The component keeps wsp_path, pt_planes, and summary as fixed outputs, then exposes Process KPI, Pr01 printability, Pr03 fresh-risk, and Pr04 deformation/failure tree outputs by detected data or right-click category toggles. Global job totals stay with Gc03/Gc05, not Pr05.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                WASPerPalette.DesignFabrication,
                "4.1_Printability")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        public override Guid ComponentGuid => new("7C4A9E2D-51B8-4F3E-A6D0-93F2C8B15E67");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Gc14_Deconstruct Path KPIs.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "WASPer Print Path to inspect for KPI / diagnostic fields. Please use the Pp01 WASPer Path from Curves before using this component.", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            foreach (var spec in FixedSpecs())
                p.AddParameter(CreateParam(spec));
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Show all KPI outputs",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle Pr05 KPI output mode");
                    _showAllOutputs = !_showAllOutputs;
                    ExpireSolution(true);
                },
                true,
                _showAllOutputs);
            Menu_AppendItem(menu, "Compact mode follows wsp_path contents", null, false);
            Menu_AppendSeparator(menu);
            foreach (var category in ToggleCategories())
            {
                string captured = category;
                Menu_AppendItem(
                    menu,
                    $"Show {captured}",
                    (sender, args) =>
                    {
                        RecordUndoEvent($"Toggle {captured}");
                        if (!_forcedCategories.Add(captured))
                            _forcedCategories.Remove(captured);
                        ExpireSolution(true);
                    },
                    true,
                    _forcedCategories.Contains(captured));
            }
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean(ShowAllKey, _showAllOutputs);
            writer.SetString(ForcedCategoriesKey, string.Join(";", _forcedCategories));
            writer.SetInt32(OutputLayoutVersionKey, OutputLayoutVersion);
            writer.SetString(OutputLayoutKeysKey, SerializeCurrentOutputLayout());
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            if (reader.ItemExists(ShowAllKey))
                _showAllOutputs = reader.GetBoolean(ShowAllKey);
            if (reader.ItemExists(ForcedCategoriesKey))
            {
                _forcedCategories.Clear();
                foreach (var category in reader.GetString(ForcedCategoriesKey).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    _forcedCategories.Add(category);
            }

            if (reader.ItemExists(OutputLayoutKeysKey))
            {
                var restoredLayout = LayoutFromSerializedKeys(reader.GetString(OutputLayoutKeysKey));
                if (restoredLayout.Count > 0)
                    ApplyOutputLayout(restoredLayout);
            }

            return base.Read(reader);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            IGH_Goo goo = null;
            if (!da.GetData(0, ref goo) || !TryExtractPath(goo, out var path) || path == null || !path.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path is required and must be a valid WASPer Print Path. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            var desired = DesiredSpecs(path);
            if (ApplyOutputLayout(desired))
            {
                Message = _showAllOutputs ? "all KPI outputs" : "compact KPIs";
                ExpireSolution(true);
                return;
            }

            SetData(da, OutputKind.WspPath, new WasperPrintPathGoo(path));
            SetTree(da, OutputKind.Planes, path.PtPlanes);

            SetTree(da, OutputKind.PrintSpeed, path.KpiPrintSpeed);
            SetTree(da, OutputKind.SegmentLength, path.KpiSegmentLength);
            SetTree(da, OutputKind.PrintVol, path.KpiPrintVol);
            SetTree(da, OutputKind.Flows, path.Flows);
            SetTree(da, OutputKind.LayerH, path.LayerH);
            SetTree(da, OutputKind.LayerW, path.LayerW);
            SetTree(da, OutputKind.LayerWf, path.LayerWf);

            SetTree(da, OutputKind.PrintLoc, path.PrintLoc);
            SetTree(da, OutputKind.PrintGlob, path.PrintGlob);
            SetTree(da, OutputKind.SupportPts, path.SupportPts);
            SetTree(da, OutputKind.SupportVects, path.SupportVects);
            SetTree(da, OutputKind.Angles, path.Angles);
            SetTree(da, OutputKind.ContactW, path.ContactWidths);

            SetTree(da, OutputKind.RiskMaterial, path.RiskMaterial);
            SetTree(da, OutputKind.RiskComb, path.RiskComb);
            SetTree(da, OutputKind.Load, path.Load);
            SetTree(da, OutputKind.Capacity, path.Capacity);

            SetTree(da, OutputKind.DRatio, path.DRatio);
            SetTree(da, OutputKind.DLoaded, path.DLoaded);
            SetTree(da, OutputKind.BendRatio, path.BendRatio);
            SetTree(da, OutputKind.SpanClass, path.SpanClass);
            SetTree(da, OutputKind.SpanLen, path.SpanLen);
            SetTree(da, OutputKind.CollapseGen, path.CollapseGen);
            SetTree(da, OutputKind.Collapsed, path.Collapsed);
            SetTree(da, OutputKind.Cascade, path.Cascade);

            SetTree(da, OutputKind.Torn, path.Torn);
            SetTree(da, OutputKind.InterfaceRatio, path.InterfaceRatio);
            SetTree(da, OutputKind.OverturnRatio, path.OverturnRatio);
            SetTree(da, OutputKind.FailureFlags, path.FailureFlags);

            WasperKpiSet pathKpis = WasperPathKpiExtractor.Extract(path, Name);
            SetData(da, OutputKind.PathKpis, new WasperKpiSetGoo(pathKpis, this));
            SetData(da, OutputKind.Summary, BuildSummary(path, desired));

            var visibleKpiGroups = desired
                .Where(s => !IsFixedOutput(s.Kind))
                .Select(s => s.Category)
                .Distinct()
                .Count();
            Message = _showAllOutputs
                ? $"all KPIs | {visibleKpiGroups} groups"
                : $"compact | {visibleKpiGroups} KPI groups";
        }

        private IReadOnlyList<OutputSpec> DesiredSpecs(WasperPrintPath path)
        {
            if (_showAllOutputs) return AllSpecs();

            bool Keep(OutputSpec spec)
            {
                if (IsFixedOutput(spec.Kind))
                    return true;

                if (CurrentOutputHasRecipients(spec.Kind))
                    return true;

                return spec.Kind switch
                {
                    OutputKind.PrintLoc => HasTree(path.PrintLoc),
                    OutputKind.PrintGlob => HasTree(path.PrintGlob),
                    OutputKind.SupportPts => HasTree(path.SupportPts),
                    OutputKind.SupportVects => HasTree(path.SupportVects),
                    OutputKind.Angles => HasTree(path.Angles),
                    OutputKind.ContactW => HasTree(path.ContactWidths),
                    OutputKind.RiskMaterial => HasTree(path.RiskMaterial),
                    OutputKind.RiskComb => HasTree(path.RiskComb),
                    OutputKind.Load => HasTree(path.Load),
                    OutputKind.Capacity => HasTree(path.Capacity),
                    OutputKind.DRatio => HasTree(path.DRatio),
                    OutputKind.DLoaded => HasTree(path.DLoaded),
                    OutputKind.BendRatio => HasTree(path.BendRatio),
                    OutputKind.SpanClass => HasTree(path.SpanClass),
                    OutputKind.SpanLen => HasTree(path.SpanLen),
                    OutputKind.CollapseGen => HasTree(path.CollapseGen),
                    OutputKind.Collapsed => HasTree(path.Collapsed),
                    OutputKind.Cascade => HasTree(path.Cascade),
                    OutputKind.Torn => HasTree(path.Torn),
                    OutputKind.InterfaceRatio => HasTree(path.InterfaceRatio),
                    OutputKind.OverturnRatio => HasTree(path.OverturnRatio),
                    OutputKind.FailureFlags => HasTree(path.FailureFlags),
                    OutputKind.PrintSpeed => HasTree(path.KpiPrintSpeed),
                    OutputKind.SegmentLength => HasTree(path.KpiSegmentLength),
                    OutputKind.PrintVol => HasTree(path.KpiPrintVol),
                    OutputKind.Flows => HasTree(path.Flows),
                    OutputKind.LayerH => HasTree(path.LayerH),
                    OutputKind.LayerW => HasTree(path.LayerW),
                    OutputKind.LayerWf => HasTree(path.LayerWf),
                    OutputKind.PathKpis => true,
                    _ => false
                } || _forcedCategories.Contains(spec.Category);
            }

            return AllSpecs().Where(Keep).ToList();
        }

        private string SerializeCurrentOutputLayout()
        {
            return string.Join(";", Params.Output.Select(p => NormalizeLegacyOutputKey(p.Name)).Where(k => !string.IsNullOrWhiteSpace(k)));
        }

        private static IReadOnlyList<OutputSpec> LayoutFromSerializedKeys(string serializedKeys)
        {
            var requested = new HashSet<string>(
                (serializedKeys ?? string.Empty)
                .Split(new[] { ';', ',', '|', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeLegacyOutputKey)
                .Where(k => !string.IsNullOrWhiteSpace(k)),
                StringComparer.Ordinal);

            requested.Add(OutputKind.WspPath.ToString());
            requested.Add(OutputKind.Planes.ToString());
            requested.Add(OutputKind.Summary.ToString());

            return AllSpecs().Where(s => requested.Contains(s.Key)).ToList();
        }

        private static string NormalizeLegacyOutputKey(string key)
        {
            return key switch
            {
                "Info" => OutputKind.Summary.ToString(),
                "Summary" => OutputKind.Summary.ToString(),
                "WspPath" => OutputKind.WspPath.ToString(),
                "wsp_path" => OutputKind.WspPath.ToString(),
                "Planes" => OutputKind.Planes.ToString(),
                "Points" => null,
                "Flows" => OutputKind.Flows.ToString(),
                "LayerH" => OutputKind.LayerH.ToString(),
                "LayerW" => OutputKind.LayerW.ToString(),
                "LayerWf" => OutputKind.LayerWf.ToString(),
                "PrintVol" => OutputKind.PrintVol.ToString(),
                "NozzleDiam" => null,
                "PrintSpeed" => OutputKind.PrintSpeed.ToString(),
                _ => Enum.TryParse(key, out OutputKind kind) ? kind.ToString() : null
            };
        }

        private bool ApplyOutputLayout(IReadOnlyList<OutputSpec> desired)
        {
            var desiredKeys = desired.Select(s => s.Key).ToList();
            var currentKeys = Params.Output.Select(p => p.Name).ToList();
            if (currentKeys.SequenceEqual(desiredKeys))
            {
                RefreshOutputMetadata(desired);
                return false;
            }

            int fixedCount = FixedSpecs().Count;
            bool fixedBlockIntact = Params.Output.Count >= fixedCount &&
                Params.Output.Take(fixedCount).Select(p => p.Name).SequenceEqual(FixedSpecs().Select(s => s.Key));

            if (!fixedBlockIntact)
            {
                for (int i = Params.Output.Count - 1; i >= 0; i--)
                    Params.UnregisterOutputParameter(Params.Output[i], true);
                foreach (var spec in FixedSpecs())
                    Params.RegisterOutputParam(CreateParam(spec));
            }
            else
            {
                for (int i = Params.Output.Count - 1; i >= fixedCount; i--)
                    Params.UnregisterOutputParameter(Params.Output[i], true);
            }

            foreach (var spec in desired.Skip(fixedCount))
                Params.RegisterOutputParam(CreateParam(spec));

            RefreshOutputMetadata(desired);
            Params.OnParametersChanged();
            return true;
        }

        private void RefreshOutputMetadata(IReadOnlyList<OutputSpec> desired)
        {
            var byKey = desired.ToDictionary(s => s.Key, s => s);
            foreach (var output in Params.Output)
            {
                if (!byKey.TryGetValue(output.Name, out var spec)) continue;
                output.NickName = spec.NickName;
                output.Description = $"[{spec.Category}] {spec.Description}";
                output.Access = spec.Access;
                output.Optional = true;
            }
        }

        private bool CurrentOutputHasRecipients(OutputKind kind)
        {
            string key = kind.ToString();
            return Params.Output.Any(p => p.Name == key && p.Recipients.Count > 0);
        }

        private int OutputIndex(OutputKind kind)
        {
            string key = kind.ToString();
            for (int i = 0; i < Params.Output.Count; i++)
                if (Params.Output[i].Name == key)
                    return i;
            return -1;
        }

        private void SetTree<T>(IGH_DataAccess da, OutputKind kind, DataTree<T> tree)
        {
            int index = OutputIndex(kind);
            if (index >= 0 && tree != null && tree.BranchCount > 0)
                da.SetDataTree(index, tree);
        }

        private void SetData(IGH_DataAccess da, OutputKind kind, object value)
        {
            int index = OutputIndex(kind);
            if (index >= 0)
                da.SetData(index, value);
        }

        private static string BuildSummary(WasperPrintPath path, IReadOnlyList<OutputSpec> visibleSpecs)
        {
            var groups = new List<string>();
            if (path.HasPlanes) groups.Add("Reference planes");
            if (path.HasPrintAssessment) groups.Add("Pr01 printability");
            if (path.HasProcessKpis) groups.Add($"Process KPIs ({KpiUnitLabel(path.KpiUnits)})");
            if (path.HasFreshRisk) groups.Add("Pr03 fresh risk");
            if (path.HasBeamDeflection) groups.Add("Pr04 deformation");
            if (path.HasFailureState) groups.Add("Pr04 failure state");
            if (groups.Count == 0) groups.Add("No KPI groups detected");

            string wcMode = !path.HasPrintAssessment ? "n/a"
                : path.NozzleDiam.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "model-based (Alhussain et al. 2024, noz_diam = {0:R})", path.NozzleDiam.Value)
                    : "geometric (full effective width baseline)";

            var visibleFields = visibleSpecs
                .GroupBy(s => s.Category)
                .Select(g => $"{g.Key}: {string.Join(", ", g.Select(s => s.NickName))}");

            return string.Format(CultureInfo.InvariantCulture,
                "wsp_Pr05_Deconstruct Path KPIs\npoints: {0}\nbranches: {1}\npresent groups: {2}\nvisible outputs: {3}\nKPI units: {4}\nWc mode: {5}\nnote: Pr05 exposes tree-shaped KPIs only; global job totals stay with Gc03/Gc05.",
                path.PointCount,
                path.BranchCount,
                string.Join(", ", groups),
                string.Join(" | ", visibleFields),
                KpiUnitLabel(path.KpiUnits),
                wcMode);
        }

        private static string KpiUnitLabel(int? units)
        {
            return units switch
            {
                1 => "cm",
                2 => "m",
                _ => "mm"
            };
        }

        private static IGH_Param CreateParam(OutputSpec spec)
        {
            IGH_Param param = spec.ParamType switch
            {
                OutputParamType.Point => new Param_Point(),
                OutputParamType.Plane => new Param_Plane(),
                OutputParamType.Number => new Param_Number(),
                OutputParamType.Boolean => new Param_Boolean(),
                OutputParamType.Vector => new Param_Vector(),
                OutputParamType.Integer => new Param_Integer(),
                OutputParamType.Text => new Param_String(),
                _ => new Param_GenericObject()
            };

            param.Name = spec.Key;
            param.NickName = spec.NickName;
            param.Description = $"[{spec.Category}] {spec.Description}";
            param.Access = spec.Access;
            param.Optional = true;
            return param;
        }

        private static IReadOnlyList<OutputSpec> FixedSpecs()
        {
            return AllSpecs().Where(s => IsFixedOutput(s.Kind)).ToList();
        }

        private static bool IsFixedOutput(OutputKind kind)
        {
            return kind == OutputKind.WspPath || kind == OutputKind.Planes || kind == OutputKind.Summary;
        }

        private static IReadOnlyList<OutputSpec> AllSpecs()
        {
            return new[]
            {
                new OutputSpec(OutputKind.WspPath, "Reference", "wsp_path", OutputParamType.Generic, GH_ParamAccess.item, "Unmodified input WASPer Print Path, passed through as a stable fixed output."),
                new OutputSpec(OutputKind.Planes, "Reference", "pt_planes", OutputParamType.Plane, GH_ParamAccess.tree, "Per-point path planes used as reference frames for the KPI values. If no planes are stored in wsp_path this output remains empty."),
                new OutputSpec(OutputKind.Summary, "Summary", "summary", OutputParamType.Text, GH_ParamAccess.item, "Point/branch counts, detected KPI groups, visible output groups, Wc mode, and the Pp04/Pr05 responsibility split."),

                new OutputSpec(OutputKind.PrintSpeed, "Process KPIs", "p_speed", OutputParamType.Number, GH_ParamAccess.tree, "Resolved per-point print speed in the path's KPI length unit per minute. G-code feedrates remain mm/min."),
                new OutputSpec(OutputKind.SegmentLength, "Process KPIs", "seg_len", OutputParamType.Number, GH_ParamAccess.tree, "Per-segment printed length in the path's KPI length unit; first item in each branch is zero."),
                new OutputSpec(OutputKind.PrintVol, "Process KPIs", "p_vol", OutputParamType.Number, GH_ParamAccess.tree, "Per-segment deposited volume in the path's KPI volume unit."),
                new OutputSpec(OutputKind.Flows, "Process KPIs", "flows", OutputParamType.Number, GH_ParamAccess.tree, "Per-point flow multipliers carried by the packed path."),
                new OutputSpec(OutputKind.LayerH, "Process KPIs", "layer_h", OutputParamType.Number, GH_ParamAccess.tree, "Per-point layer height carried by the packed path."),
                new OutputSpec(OutputKind.LayerW, "Process KPIs", "layer_w", OutputParamType.Number, GH_ParamAccess.tree, "Nominal/base bead width carried by the packed path."),
                new OutputSpec(OutputKind.LayerWf, "Process KPIs", "layer_wf", OutputParamType.Number, GH_ParamAccess.tree, "Flow-adjusted deposited bead width carried by the packed path."),

                new OutputSpec(OutputKind.PrintLoc, "Pr01 Printability", "print_loc", OutputParamType.Number, GH_ParamAccess.tree, "Local printability, 1 printable / 0 not."),
                new OutputSpec(OutputKind.PrintGlob, "Pr01 Printability", "print_glob", OutputParamType.Boolean, GH_ParamAccess.tree, "Support-chain global printability."),
                new OutputSpec(OutputKind.SupportPts, "Pr01 Printability", "support_pts", OutputParamType.Point, GH_ParamAccess.tree, "Closest support point on the previous layer."),
                new OutputSpec(OutputKind.SupportVects, "Pr01 Printability", "support_vects", OutputParamType.Vector, GH_ParamAccess.tree, "Point minus support overhang vector."),
                new OutputSpec(OutputKind.Angles, "Pr01 Printability", "angles", OutputParamType.Number, GH_ParamAccess.tree, "Overhang angle vs gravity in degrees."),
                new OutputSpec(OutputKind.ContactW, "Pr01 Printability", "Wc", OutputParamType.Number, GH_ParamAccess.tree, "Contact width with the previous layer."),

                new OutputSpec(OutputKind.RiskMaterial, "Pr03 Fresh Risk", "risk_mat", OutputParamType.Number, GH_ParamAccess.tree, "Fresh material risk, demand/capacity."),
                new OutputSpec(OutputKind.RiskComb, "Pr03 Fresh Risk", "risk_comb", OutputParamType.Number, GH_ParamAccess.tree, "Combined material/geometric risk."),
                new OutputSpec(OutputKind.Load, "Pr03 Fresh Risk", "load", OutputParamType.Number, GH_ParamAccess.tree, "Accumulated fresh load per point in N."),
                new OutputSpec(OutputKind.Capacity, "Pr03 Fresh Risk", "capacity", OutputParamType.Number, GH_ParamAccess.tree, "Contact capacity per point in N."),

                new OutputSpec(OutputKind.DRatio, "Pr04 Deformation", "d_ratio", OutputParamType.Number, GH_ParamAccess.tree, "Span deflection ratio delta/layer_h from self-weight."),
                new OutputSpec(OutputKind.DLoaded, "Pr04 Deformation", "d_loaded", OutputParamType.Number, GH_ParamAccess.tree, "Span deflection ratio including Pr03 accumulated load."),
                new OutputSpec(OutputKind.BendRatio, "Pr04 Deformation", "bend_ratio", OutputParamType.Number, GH_ParamAccess.tree, "Span bending stress / tau_y-derived flexural strength."),
                new OutputSpec(OutputKind.SpanClass, "Pr04 Deformation", "span_class", OutputParamType.Integer, GH_ParamAccess.tree, "0 supported / 1 bridge / 2 cantilever."),
                new OutputSpec(OutputKind.SpanLen, "Pr04 Deformation", "span_len", OutputParamType.Number, GH_ParamAccess.tree, "Unsupported span length in model units."),
                new OutputSpec(OutputKind.CollapseGen, "Pr04 Deformation", "collapse_gen", OutputParamType.Integer, GH_ParamAccess.tree, "Collapse generation: -1 stable, 0 direct collapse, 1+ upward cascade."),
                new OutputSpec(OutputKind.Collapsed, "Pr04 Deformation", "collapsed", OutputParamType.Boolean, GH_ParamAccess.tree, "Physical collapse state from full-layer sag or bending yield."),
                new OutputSpec(OutputKind.Cascade, "Pr04 Deformation", "cascade", OutputParamType.Boolean, GH_ParamAccess.tree, "True where collapse was caused by failed support below."),

                new OutputSpec(OutputKind.Torn, "Pr04 Failure State", "torn", OutputParamType.Boolean, GH_ParamAccess.tree, "True at point i when bead edge i to i+1 is separated."),
                new OutputSpec(OutputKind.InterfaceRatio, "Pr04 Failure State", "interface_ratio", OutputParamType.Number, GH_ParamAccess.tree, "Supported-interface demand divided by estimated fresh contact capacity."),
                new OutputSpec(OutputKind.OverturnRatio, "Pr04 Failure State", "overturn_ratio", OutputParamType.Number, GH_ParamAccess.tree, "Support eccentricity divided by no-tension kern limit."),
                new OutputSpec(OutputKind.FailureFlags, "Pr04 Failure State", "failure_flags", OutputParamType.Integer, GH_ParamAccess.tree, "Mechanism bit mask: 1 span deflection, 2 bending yield, 4 tear, 8 interface failure, 16 overturning."),

                new OutputSpec(OutputKind.PathKpis, "Global KPIs", "path_kpis", OutputParamType.Generic, GH_ParamAccess.item, "Global fabrication KPI set extracted through the shared WasperPathKpiExtractor for Ut17 and reporting components.")
            };
        }

        private static IReadOnlyList<string> ToggleCategories()
        {
            return new[]
            {
                "Process KPIs",
                "Pr01 Printability",
                "Pr03 Fresh Risk",
                "Pr04 Deformation",
                "Pr04 Failure State"
            };
        }

        private static bool HasTree<T>(DataTree<T> tree)
        {
            return tree != null && tree.BranchCount > 0;
        }

        private static bool TryExtractPath(IGH_Goo goo, out WasperPrintPath path)
        {
            path = null;
            if (goo is WasperPrintPathGoo pathGoo) path = pathGoo.Value;
            else if (goo is GH_ObjectWrapper wrapper) path = wrapper.Value as WasperPrintPath;
            return path != null;
        }

        private enum OutputKind
        {
            WspPath,
            Planes,
            PrintSpeed,
            SegmentLength,
            PrintVol,
            Flows,
            LayerH,
            LayerW,
            LayerWf,
            PrintLoc,
            PrintGlob,
            SupportPts,
            SupportVects,
            Angles,
            ContactW,
            RiskMaterial,
            RiskComb,
            Load,
            Capacity,
            DRatio,
            DLoaded,
            BendRatio,
            SpanClass,
            SpanLen,
            CollapseGen,
            Collapsed,
            Cascade,
            Torn,
            InterfaceRatio,
            OverturnRatio,
            FailureFlags,
            PathKpis,
            Summary
        }

        private enum OutputParamType
        {
            Generic,
            Point,
            Plane,
            Number,
            Boolean,
            Vector,
            Integer,
            Text
        }

        private sealed class OutputSpec
        {
            public OutputSpec(OutputKind kind, string category, string nickName, OutputParamType paramType, GH_ParamAccess access, string description)
            {
                Kind = kind;
                Category = category;
                NickName = nickName;
                ParamType = paramType;
                Access = access;
                Description = description;
            }

            public OutputKind Kind { get; }
            public string Key => Kind.ToString();
            public string Category { get; }
            public string NickName { get; }
            public OutputParamType ParamType { get; }
            public GH_ParamAccess Access { get; }
            public string Description { get; }
        }
    }
}
