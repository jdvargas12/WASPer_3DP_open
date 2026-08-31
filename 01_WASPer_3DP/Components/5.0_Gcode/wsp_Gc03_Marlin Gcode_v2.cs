#region Component Description
/*
Component: wsp_Gc03_Marlin Gcode (From points)
Nickname: Marlin Gcode
Category: WASPer_3DP
SubCategory: 5.0_Gcode
Version:
    Uses the compiled assembly version in the component message via _versionTag.
    LDM path: C# port of GhPython wsp_Gc03 LDM v1.0.4 (260310).
    FDM path: C# port of GhPython wsp_Gc03 FDM v1.0.4 (260207).

GENERAL DESCRIPTION
Generates Marlin-compatible G-code from the packed WASPer Print Path structured as
{layer ; curve}. ONE component serves both printing processes; the wired
3dp_params object selects the code path:

  - wsp_Gc01_LDM 3DP Parameters (Process = LDM):
      relative extrusion from nozzle cross-section, Z-hop travel, volume-based
      file splitting, time_correction, full LDM statistics header.
  - wsp_Gc02_FDM 3DP Parameters (Process = FDM):
      filament E-axis conversion (E = V / A_fil, 5 decimals), fan control per
      layer (M106), nozzle/bed temperature blocks, optional custom start/end
      G-code, FDM statistics header. No file splitting.

The component intentionally has NO scalar parameter inputs — all printing
parameters travel inside 3dp_params, keeping the canvas clean. When
3dp_params is not wired, the component runs with LDM defaults; only
nozzle_diameter has no default, so a clear error asks for a params object.

INPUTS
0) sample_info : list[str] (opt)        User comments added to the G-code header
                                        (normalized to ';' comments).
1) wsp_path    : WasperPrintPath       Packed canonical pt_planes / flows /
                                        layer_h from Pp01; sole path source.
2) 3dp_params  : Wasper3dpParams (opt)  Printing parameters + process selector.
                                        Unwired -> LDM defaults (nozzle required).

OUTPUTS
0) g_code          tree[str]     LDM: one branch per output file ({0} when not
                                 split). FDM: single branch {0}.
1) printing_points tree[Point3d] Pass-through of the points actually used.
2) printing_speed  tree[double]  Resolved per-point print feedrate.
3) p_time_min      double        Estimated print time [min] (LDM: corrected).
4) p_path_len_mm   double        Total printed path length [mm].
5) p_volume_cm3    double        Total extruded volume [cm3].
6) p_mass_kg       double        Deposited mass [kg] when wsp_mat density is available.
7) layers          int           Total number of layers.
8) wsp_path        WasperPrintPath
                                 Enriched path with resolved nozzle/print/travel/
                                 Z-hop data and the ordered job motion plan.

PORT NOTES (vs the GhPython versions)
- All numeric G-code formatting uses InvariantCulture (decimal point guaranteed).
- Feedrates are written without a trailing ".0" (Marlin accepts both).
- LDM vol_treshold_L defaults to 4.5 L as documented (the Python __init__
  carried a leftover 2 L default contradicting its own docstring).
- z_hop defaults to 0/off; input a value > 0 in 3dp_params to enable positive-Z hop moves.
- FDM: sample_info lines are appended to the printing information header
  (the FDM Python version had no sample_info input).
- FDM: fan_speed with a single branch of N values follows the per-layer list
  rule (last value repeats); {layer}/{layer;0} trees follow the tree rule.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using WASPer_3DP;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public sealed class wsp_Gc03_Marlin_Gcode_From_Points : GH_Component
    {
        private const string ShowAllOutputsKey = "wsp_gc04_show_all_outputs";
        private readonly string _versionTag;
        private bool _showAllOutputs;

        public wsp_Gc03_Marlin_Gcode_From_Points()
            : base(
                "wsp_Gc03_Marlin Gcode v2",
                "Marlin Gcode",
                "Generates Marlin-compatible G-code from a packed WASPer Print Path. " +
                "The wired 3dp_params object selects the process: LDM (wsp_Gc01_LDM 3DP " +
                "Parameters — splitting, time correction) or FDM (wsp_Gc02_FDM 3DP Parameters " +
                "— fan, temperatures, custom start/end blocks).\r\n" +
                "wsp_path is the primary input and output; G-code remains in machine mm/mm-min units. " +
                "kpi_units controls only the reported/packed KPI units and never rescales generated G-code. " +
                "When 3dp_params contains density from a connected WASPer Material, Gc03 also calculates and packs deposited mass in kg. " +
                "Unwired 3dp_params runs with LDM defaults. " +
                "For the LDM E-axis conversion: E = flow * (nozzle_diameter * layer_h * segment_length) / A_fil, where A_fil = pi * (fillament_multi / 2)^2.\r\n\r\n" +
                "PRINTABILITY WARNING: a continuous Shell does not automatically adapt separate interior paths. Gc03 warns about possible intersections, protrusions, lost support, and nozzle collisions, but does not block G-code generation.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("142DE754-E5CC-4178-A52C-CF85BF19C783");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Gc04_Marlin Gcode.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        #region Register IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            int i;

            // 0
            i = p.AddGenericParameter("wasper_path", "wsp_path",
                "WASPer Print Path object from Pp01 using canonical pt_planes (origins are path points), flows " +
                "layer_h, layer_w, layer_wf, print_vol, nozzle_diam, and optional Pp08 " +
                "print_speed values. Packed process metadata is preserved and used when " +
                "the corresponding explicit 3dp_params value is absent. LayerW is the nominal/base bead width; LayerWf is the flow-adjusted effective deposited width. " +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            // 1
            i = p.AddGenericParameter("3dp_params", "3dp_params",
                "WASPer 3DP Params object selecting the process and carrying all " +
                "printing parameters: wsp_Gc01_LDM 3DP Parameters (Process = LDM) or " +
                "wsp_Gc02_FDM 3DP Parameters (Process = FDM). When not wired, the " +
                "component runs with LDM defaults; nozzle_diameter has no default, " +
                "so a params object with it set is effectively required. LDM uses " +
                "E = flow * (nozzle_diameter * layer_h * segment_length) / A_fil, " +
                "with A_fil = pi * (fillament_multi / 2)^2; FDM uses E = V / A_fil. " +
                "A connected wsp_mat in Gc01/Gc02 supplies density for the optional mass KPI; process fallback density does not create a public mass KPI.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            // 2
            i = p.AddTextParameter("sample_info", "sample_info",
                "Optional user comments added to the G-code header. " +
                "Lines are normalized to valid G-code comments (';' prefixed).",
                GH_ParamAccess.list);
            p[i].Optional = true;

            // 3
            i = p.AddIntegerParameter("kpi_units", "kpi_units",
                "Units used only for Gc03 KPI outputs and the KPI fields packed into wsp_path. " +
                "This does not change generated G-code coordinates or feedrates, which remain Marlin machine units (mm and mm/min). " +
                "0 = mm/mm²/mm³, 1 = cm/cm²/cm³, 2 = m/m²/m³. Default: 0.",
                GH_ParamAccess.item, 0);
            p[i].Optional = true;
        }
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            RegisterCompactOutputParams(p);
        }
        #endregion

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Show all outputs", (sender, args) =>
            {
                RecordUndoEvent("Toggle Gc03 outputs");
                _showAllOutputs = !_showAllOutputs;
                RebuildOutputs();
                ExpireSolution(true);
            }, true, _showAllOutputs);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean(ShowAllOutputsKey, _showAllOutputs);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            bool result = base.Read(reader);
            _showAllOutputs = reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey);
            RebuildOutputs();
            return result;
        }

        private void RebuildOutputs()
        {
            while (Params.Output.Count > 4)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

            if (Params.Output.Count < 4)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);
                RegisterCompactOutputParams();
            }

            if (_showAllOutputs)
            {
                WasperPathDebugOutputs.RegisterCore(
                    this,
                    new[] { "pt_planes" });
                RegisterDebugOutputParams();
            }
            Params.OnParametersChanged();
        }

        private static void RegisterCompactOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wasper_path", "wsp_path",
                "WASPer Print Path enriched with generated machine metadata, motion plan, and KPI fields.",
                GH_ParamAccess.item);
            p.AddTextParameter("g_code", "gcode",
                "Generated G-code lines. G-code coordinates and feedrates remain machine units: mm and mm/min. LDM: one branch per output file ({0} when not split). FDM: single branch {0}.",
                GH_ParamAccess.tree);
            p.AddPlaneParameter("point_planes", "p_planes",
                "Canonical printing planes carried by the generated path; their origins are the path points.",
                GH_ParamAccess.tree);
            p.AddPlaneParameter("travel_planes", "t_planes",
                "Travel/debug planes generated from the Gc03 motion plan. Branches are organized as {layer; travel_index}; Z-hop moves are included.",
                GH_ParamAccess.tree);
        }

        private void RegisterCompactOutputParams()
        {
            Params.RegisterOutputParam(new Param_GenericObject
            {
                Name = "wasper_path",
                NickName = "wsp_path",
                Description = "WASPer Print Path enriched with generated machine metadata, motion plan, and KPI fields.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Param_String
            {
                Name = "g_code",
                NickName = "gcode",
                Description = "Generated G-code lines. G-code coordinates and feedrates remain machine units: mm and mm/min.",
                Access = GH_ParamAccess.tree
            });
            Params.RegisterOutputParam(new Param_Plane
            {
                Name = "point_planes",
                NickName = "p_planes",
                Description = "Canonical printing planes carried by the generated path; their origins are the path points.",
                Access = GH_ParamAccess.tree
            });
            Params.RegisterOutputParam(new Param_Plane
            {
                Name = "travel_planes",
                NickName = "t_planes",
                Description = "Travel/debug planes generated from the Gc03 motion plan. Branches are organized as {layer; travel_index}; Z-hop moves are included.",
                Access = GH_ParamAccess.tree
            });
        }

        private void RegisterDebugOutputParams()
        {
            Params.RegisterOutputParam(new Param_Number { Name = "printing_speed", NickName = "p_speed", Description = "Resolved per-point print speed converted to kpi_units length/min.", Access = GH_ParamAccess.tree });
            Params.RegisterOutputParam(new Param_Number { Name = "p_time_min", NickName = "p_time_min", Description = "Total estimated print time in minutes (LDM: time_correction applied).", Access = GH_ParamAccess.item });
            Params.RegisterOutputParam(new Param_Number { Name = "path_length", NickName = "path_length", Description = "Total printed path length converted to kpi_units length.", Access = GH_ParamAccess.item });
            Params.RegisterOutputParam(new Param_Number { Name = "p_vol", NickName = "p_vol", Description = "Total deposited volume converted to kpi_units volume.", Access = GH_ParamAccess.item });
            Params.RegisterOutputParam(new Param_Number { Name = "p_mass", NickName = "p_mass", Description = "Total deposited mass in kg. Available only when 3dp_params contains density from a connected WASPer Material.", Access = GH_ParamAccess.item });
            Params.RegisterOutputParam(new Param_Integer { Name = "layers", NickName = "layers", Description = "Total number of layers.", Access = GH_ParamAccess.item });
        }

        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var gTree = new DataTree<string>();
            var pointsOut = new DataTree<Point3d>();
            var speedOut = new DataTree<double>();
            DataTree<Plane> planesOut = null;
            DataTree<double> flowsOut = null;
            DataTree<double> layerHOut = null;
            WasperPrintPath wp = null;
            WasperMotionPlan motionPlan = null;
            double? resolvedNozzleDiameter = null;
            double? resolvedTravelSpeed = null;
            double? resolvedZHop = null;
            double? resolvedZHopSpeed = null;
            double? explicitLayerWOverride = null;
            double? materialDensityKgM3 = null;
            double pTimeMin = 0.0, pPathLen = 0.0, pVolume = 0.0;
            int layers = 0;
            int kpiUnits = 0;
            KpiUnitScale kpiScale = KpiUnitScale.FromCode(0);

            try
            {
                // ---- 0 wsp_path ----------------------------------------------------
                wp = ReadGoo<WasperPrintPath, WasperPrintPathGoo>(DA, 0, "wsp_path");
                if (wp != null && wp.IsPartial)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "Input wsp_path is marked partial. Gc03 will generate G-code/KPIs only for the partial/reconstructed path state, not necessarily the full original print.");
                }
                if (WasperGcodeTreeUtil.TryGetContinuousShellInteriorWarning(
                        wp,
                        out string continuousShellWarning))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        continuousShellWarning +
                        " Gc03 will still generate G-code because this condition requires design review rather than automatic rejection.");
                }

                // The packed path is now the sole source for path data.
                DataTree<Point3d> points = wp != null && wp.HasPoints ? wp.Points : null;
                DataTree<Plane> planes = wp != null && wp.HasPlanes ? wp.PtPlanes : null;
                DataTree<double> flows = wp != null && wp.HasFlows ? wp.Flows : null;
                DataTree<double> layerH = wp != null && wp.HasLayerH ? wp.LayerH : null;

                planesOut = planes;
                flowsOut = flows;
                layerHOut = layerH;

                // ---- 1 3dp_params (unwired -> LDM defaults) -----------------------
                Wasper3dpParams prm =
                    ReadGoo<Wasper3dpParams, Wasper3dpParamsGoo>(DA, 1, "3dp_params")
                    ?? new Wasper3dpParams { Process = Wasper3dpProcess.LDM };
                if (prm.Density.HasValue &&
                    double.IsFinite(prm.Density.Value) && prm.Density.Value > 0.0)
                {
                    materialDensityKgM3 = prm.Density.Value;
                }

                // ---- 2 sample_info -------------------------------------------------
                var sampleInfoRaw = new List<string>();
                DA.GetDataList(2, sampleInfoRaw);

                // ---- 3 kpi_units ---------------------------------------------------
                DA.GetData(3, ref kpiUnits);
                kpiScale = KpiUnitScale.FromCode(kpiUnits);
                kpiUnits = kpiScale.Code;
                explicitLayerWOverride = prm.LayerW;
                if (wp != null && wp.NozzleDiam.HasValue)
                {
                    if (!prm.NozzleDiameter.HasValue)
                    {
                        prm = prm.Clone();
                        prm.NozzleDiameter = wp.NozzleDiam.Value;
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "Resolved nozzle_diam from wsp_path.NozzleDiam because 3dp_params did not provide one.");
                    }
                    else if (Math.Abs(prm.NozzleDiameter.Value - wp.NozzleDiam.Value) > 1e-12)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "Override applied: explicit 3dp_params nozzle_diameter replaced wsp_path.NozzleDiam.");
                    }
                }
                if (!prm.HasPrintSpeed && wp != null && wp.HasPrintSpeed)
                {
                    // Do not mutate the upstream Gc01/Gc02 object: Grasshopper may reuse it
                    // after the path input changes from a Pp08 path to a normal path.
                    prm = prm.Clone();
                    prm.PrintSpeed = wp.PrintSpeed;
                }
                if (!prm.LayerW.HasValue && wp != null && wp.HasLayerW)
                {
                    prm = prm.Clone();
                    prm.LayerW = RepresentativeLayerWidth(wp.LayerW, 0.0);
                }

                // Pr01 printability is embedded in wsp_path. Use its full-resolution
                // local score for the existing G-code header statistics.
                DataTree<double> kpi = wp != null && wp.HasPrintAssessment
                    ? wp.PrintLoc
                    : null;

                bool isFdm = prm.Process == Wasper3dpProcess.FDM;
                resolvedNozzleDiameter = prm.NozzleDiameter;
                resolvedTravelSpeed = Gc.NumOrDefault(
                    prm.TravelSpeed, isFdm ? 5000.0 : 8000.0);
                resolvedZHop = prm.ZHop ?? 0.0;
                resolvedZHopSpeed = Gc.NumOrDefault(
                    prm.ZHopSpeed, isFdm ? 3000.0 : 6000.0);
                Message = $"{_versionTag} - {(isFdm ? "FDM" : "LDM")}";

                // ---- generate ------------------------------------------------------
                if (isFdm)
                {
                    var gen = new FdmGcodeGenerator(
                        this, _versionTag,
                        points, flows, layerH,
                        prm, prm.Density, sampleInfoRaw, kpi, wp?.PrintVol, wp?.StrokeIds);

                    if (gen.Validate())
                    {
                        gen.Run();
                        gTree    = gen.OutputGcodeTree;
                        speedOut = gen.SpeedOutTree;
                        pTimeMin = gen.PTimeMin;
                        pPathLen = gen.PPathLenMm;
                        pVolume  = gen.PVolumeCm3;
                        layers   = gen.Layers;
                        if (points != null) pointsOut = points;
                        motionPlan = WasperMotionPlanBuilder.Build(
                            pointsOut, speedOut,
                            resolvedTravelSpeed.Value,
                            resolvedZHop.Value,
                            resolvedZHopSpeed.Value,
                            wp?.StrokeIds);
                    }
                    else
                    {
                        gTree.Add("; Validation failed — check GH error messages above.", new GH_Path(0));
                    }
                }
                else
                {
                    var gen = new LdmGcodeGenerator(
                        this, _versionTag,
                        points, flows, layerH,
                        prm, prm.Density, sampleInfoRaw, kpi, wp?.PrintVol, wp?.StrokeIds);

                    if (gen.Validate())
                    {
                        gen.Run();
                        gTree    = gen.OutputGcodeTree;
                        speedOut = gen.SpeedOutTree;
                        pTimeMin = gen.PTimeMin;
                        pPathLen = gen.PPathLenMm;
                        pVolume  = gen.PVolumeCm3;
                        layers   = gen.Layers;
                        if (points != null) pointsOut = points;
                        motionPlan = WasperMotionPlanBuilder.Build(
                            pointsOut, speedOut,
                            resolvedTravelSpeed.Value,
                            resolvedZHop.Value,
                            resolvedZHopSpeed.Value,
                            wp?.StrokeIds);
                    }
                    else
                    {
                        gTree.Add("; Validation failed — check GH error messages above.", new GH_Path(0));
                    }
                }
            }
            catch (Exception e)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"wsp_Gc03 unexpected error: {e.Message}");
                gTree = new DataTree<string>();
                gTree.Add($"; UNEXPECTED ERROR: {e.Message}", new GH_Path(0));
            }

            BuildResolvedWidthMetadata(
                pointsOut, flowsOut, layerHOut,
                wp?.LayerW, wp?.LayerWf, wp?.PrintVol,
                explicitLayerWOverride,
                out var resolvedLayerWTree,
                out var resolvedLayerWfTree,
                out var resolvedPrintVolTree);
            var kpiSpeedTree = ConvertNumberTree(speedOut, kpiScale.LengthFactor);
            var kpiSegmentLengthTree = BuildSegmentLengthTree(pointsOut, kpiScale.LengthFactor);
            var kpiPrintVolTree = ConvertNumberTree(resolvedPrintVolTree, kpiScale.VolumeFactorFromMm3);
            var travelPlanes = BuildTravelPlaneTree(motionPlan);
            double kpiPathLength = Math.Round(pPathLen * kpiScale.LengthFactor, 4);
            double kpiVolume = Math.Round((pVolume * 1000.0) * kpiScale.VolumeFactorFromMm3, 4);
            double? kpiMassKg = materialDensityKgM3.HasValue &&
                                double.IsFinite(pVolume) && pVolume >= 0.0
                ? Math.Round(materialDensityKgM3.Value * (pVolume / 1_000_000.0), 6)
                : (double?)null;

            var enrichedPath = new WasperPrintPath(
                    pointsOut, planesOut, flowsOut, layerHOut,
                    speedOut != null && speedOut.DataCount > 0 ? speedOut : null,
                    wp?.PrintLoc, wp?.PrintGlob, wp?.SupportPts, wp?.SupportVects,
                    wp?.Angles, wp?.ContactWidths, wp?.RiskMaterial, wp?.RiskComb,
                    wp?.Load, wp?.Capacity, resolvedNozzleDiameter,
                    wp?.DRatio, wp?.DLoaded, wp?.BendRatio, wp?.SpanClass, wp?.SpanLen,
                    wp?.Collapsed, wp?.Cascade, wp?.CollapseGen,
                    resolvedLayerWTree, resolvedLayerWfTree, resolvedPrintVolTree,
                    wp?.Torn, wp?.InterfaceRatio, wp?.OverturnRatio, wp?.FailureFlags,
                    resolvedTravelSpeed, resolvedZHop, resolvedZHopSpeed, motionPlan,
                    kpiUnits, kpiSegmentLengthTree, kpiSpeedTree, kpiPrintVolTree,
                    pTimeMin, kpiPathLength, kpiVolume, layers,
                    isPartial: wp?.IsPartial ?? false,
                    pathRoles: wp?.PathRoles,
                    layerPlanes: wp?.LayerPlanes,
                    strokeIds: wp?.StrokeIds,
                    hasCrossLayerShellContinuity: wp?.HasCrossLayerShellContinuity ?? false,
                    kpiMassKg: kpiMassKg);

            SetOutputData(DA, "wasper_path", new WasperPrintPathGoo(enrichedPath));
            SetOutputTree(DA, "g_code", gTree);
            SetOutputTree(DA, "point_planes", planesOut);
            SetOutputTree(DA, "travel_planes", travelPlanes);
            WasperPathDebugOutputs.SetCore(DA, this, enrichedPath);
            SetOutputTree(DA, "printing_speed", kpiSpeedTree);
            SetOutputData(DA, "p_time_min", pTimeMin);
            SetOutputData(DA, "path_length", kpiPathLength);
            SetOutputData(DA, "p_vol", kpiVolume);
            if (kpiMassKg.HasValue)
                SetOutputData(DA, "p_mass", kpiMassKg.Value);
            SetOutputData(DA, "layers", layers);

            Message = $"{Message} | KPI {kpiScale.Label}";
        }
        #endregion

        #region Input helpers
        /// <summary>Reads an optional generic input wrapping a WASPer data class.</summary>
        private TVal ReadGoo<TVal, TGoo>(IGH_DataAccess DA, int index, string name)
            where TVal : class
            where TGoo : GH_Goo<TVal>
        {
            IGH_Goo goo = null;
            if (!DA.GetData(index, ref goo) || goo == null) return null;

            if (goo is TGoo typed) return typed.Value;
            if (goo is GH_ObjectWrapper w && w.Value is TVal direct) return direct;

            TVal casted = null;
            if (goo.CastTo(out casted) && casted != null) return casted;

            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"'{name}' received a {goo.TypeName} which is not a valid WASPer object; input ignored. " +
                (string.Equals(name, "wsp_path", StringComparison.OrdinalIgnoreCase)
                    ? "Please use the Pp01 WASPer Path from Curves before using this component."
                    : string.Empty));
            return null;
        }

        private int OutputIndex(string name)
        {
            for (int i = 0; i < Params.Output.Count; i++)
                if (Params.Output[i].Name == name)
                    return i;
            return -1;
        }

        private void SetOutputData(IGH_DataAccess da, string name, object value)
        {
            int index = OutputIndex(name);
            if (index >= 0)
                da.SetData(index, value);
        }

        private void SetOutputTree<T>(IGH_DataAccess da, string name, DataTree<T> tree)
        {
            int index = OutputIndex(name);
            if (index >= 0 && tree != null)
                da.SetDataTree(index, tree);
        }

        #endregion

        private readonly struct KpiUnitScale
        {
            public KpiUnitScale(int code, string label, double lengthFactor)
            {
                Code = code;
                Label = label;
                LengthFactor = lengthFactor;
                VolumeFactorFromMm3 = lengthFactor * lengthFactor * lengthFactor;
            }

            public int Code { get; }
            public string Label { get; }
            public double LengthFactor { get; }
            public double VolumeFactorFromMm3 { get; }

            public static KpiUnitScale FromCode(int code)
            {
                return code switch
                {
                    1 => new KpiUnitScale(1, "cm", 0.1),
                    2 => new KpiUnitScale(2, "m", 0.001),
                    _ => new KpiUnitScale(0, "mm", 1.0)
                };
            }
        }

        private static DataTree<double> ConvertNumberTree(DataTree<double> source, double factor)
        {
            var result = new DataTree<double>();
            if (source == null) return result;

            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                foreach (double value in source.Branch(path))
                    result.Add(double.IsFinite(value) ? value * factor : value, path);
            }

            return result;
        }

        private static DataTree<double> BuildSegmentLengthTree(DataTree<Point3d> points, double lengthFactor)
        {
            var result = new DataTree<double>();
            if (points == null) return result;

            for (int b = 0; b < points.BranchCount; b++)
            {
                GH_Path path = points.Paths[b];
                var branch = points.Branch(path);
                int count = branch?.Count ?? 0;
                for (int i = 0; i < count; i++)
                {
                    double length = i > 0 ? branch[i - 1].DistanceTo(branch[i]) : 0.0;
                    result.Add(length * lengthFactor, path);
                }
            }

            return result;
        }

        private static DataTree<Plane> BuildTravelPlaneTree(WasperMotionPlan motionPlan)
        {
            var result = new DataTree<Plane>();
            if (motionPlan == null || motionPlan.Count == 0) return result;

            int travelIndex = -1;
            WasperMotion previousTravel = null;
            Plane previousPlane = Plane.WorldXY;

            foreach (var motion in motionPlan.Motions)
            {
                if (motion.Type == WasperMotionType.Print)
                {
                    previousTravel = null;
                    continue;
                }

                bool newBranch = previousTravel == null ||
                    previousTravel.LayerIndex != motion.LayerIndex ||
                    previousTravel.Type == WasperMotionType.Print;
                if (newBranch) travelIndex++;

                GH_Path path = new GH_Path(Math.Max(0, motion.LayerIndex), Math.Max(0, travelIndex));
                Plane startPlane = PlaneFromMotionPoint(motion.From, motion.To - motion.From, previousPlane);
                Plane endPlane = PlaneFromMotionPoint(motion.To, motion.To - motion.From, startPlane);
                result.Add(startPlane, path);
                result.Add(endPlane, path);

                previousPlane = endPlane;
                previousTravel = motion;
            }

            return result;
        }

        private static Plane PlaneFromMotionPoint(Point3d origin, Vector3d direction, Plane fallback)
        {
            Vector3d x = direction;
            if (!x.Unitize() || Math.Abs(Vector3d.Multiply(x, Vector3d.ZAxis)) > 0.98)
                x = fallback.IsValid ? fallback.XAxis : Vector3d.XAxis;
            x.Z = 0.0;
            if (!x.Unitize()) x = Vector3d.XAxis;
            Vector3d y = Vector3d.CrossProduct(Vector3d.ZAxis, x);
            if (!y.Unitize()) y = Vector3d.YAxis;
            return new Plane(origin, x, y);
        }

        private static void BuildResolvedWidthMetadata(
            DataTree<Point3d> points,
            DataTree<double> flows,
            DataTree<double> heights,
            DataTree<double> incomingLayerW,
            DataTree<double> incomingLayerWf,
            DataTree<double> incomingPrintVol,
            double? explicitLayerW,
            out DataTree<double> layerW,
            out DataTree<double> layerWf,
            out DataTree<double> printVol)
        {
            layerW = new DataTree<double>();
            layerWf = new DataTree<double>();
            printVol = new DataTree<double>();

            if (points == null || points.BranchCount == 0)
                return;

            const double tol = 1e-9;
            bool overrideWidth = explicitLayerW.HasValue && explicitLayerW.Value > tol && double.IsFinite(explicitLayerW.Value);

            for (int b = 0; b < points.BranchCount; b++)
            {
                GH_Path path = points.Paths[b];
                var pointBranch = points.Branch(path);
                int count = pointBranch?.Count ?? 0;
                var flowBranch = flows != null && flows.PathExists(path) ? flows.Branch(path) : null;
                var heightBranch = heights != null && heights.PathExists(path) ? heights.Branch(path) : null;
                var incomingWBranch = incomingLayerW != null && incomingLayerW.PathExists(path) ? incomingLayerW.Branch(path) : null;
                var incomingWfBranch = incomingLayerWf != null && incomingLayerWf.PathExists(path) ? incomingLayerWf.Branch(path) : null;
                var incomingVolBranch = incomingPrintVol != null && incomingPrintVol.PathExists(path) ? incomingPrintVol.Branch(path) : null;

                var nominal = new double[count];
                var effective = new double[count];

                bool canPreserveEffective = !overrideWidth && incomingWfBranch != null && incomingWfBranch.Count > 0;
                bool canPreservePrintVol = !overrideWidth && incomingVolBranch != null && incomingVolBranch.Count > 0;

                for (int i = 0; i < count; i++)
                {
                    double height = GetTreeValue(heightBranch, i, 0.0);
                    double flow = GetTreeValue(flowBranch, i, 1.0);
                    double nominalWidth = overrideWidth
                        ? explicitLayerW.Value
                        : GetTreeValue(incomingWBranch, i, height * 2.5);

                    if (nominalWidth <= tol || !double.IsFinite(nominalWidth))
                        nominalWidth = Math.Max(tol * 10.0, height * 2.5);

                    double effectiveWidth = canPreserveEffective
                        ? GetTreeValue(incomingWfBranch, i, double.NaN)
                        : double.NaN;

                    if (effectiveWidth <= tol || !double.IsFinite(effectiveWidth))
                        effectiveWidth = EstimateFlowAdjustedWidth(nominalWidth, height, flow, tol);

                    nominal[i] = nominalWidth;
                    effective[i] = effectiveWidth;
                    layerW.Add(nominalWidth, path);
                    layerWf.Add(effectiveWidth, path);
                }

                for (int i = 0; i < count; i++)
                {
                    double volume = canPreservePrintVol
                        ? GetTreeValue(incomingVolBranch, i, double.NaN)
                        : double.NaN;

                    if (volume < 0.0 || !double.IsFinite(volume))
                    {
                        volume = 0.0;
                        if (i > 0)
                        {
                            double length = pointBranch[i - 1].DistanceTo(pointBranch[i]);
                            double height = GetTreeValue(heightBranch, i, 0.0);
                            double area = BeadArea(effective[i], height, tol);
                            if (length > tol && area > 0.0 && double.IsFinite(length))
                                volume = length * area;
                        }
                    }

                    printVol.Add(volume, path);
                }
            }
        }

        private static double RepresentativeLayerWidth(DataTree<double> tree, double fallback)
        {
            if (tree == null || tree.DataCount == 0) return fallback;
            var values = tree.AllData().Where(v => v > 0.0 && double.IsFinite(v)).ToList();
            if (values.Count == 0) return fallback;
            values.Sort();
            return values[values.Count / 2];
        }

        private static double GetTreeValue(IList<double> branch, int itemIndex, double fallback)
        {
            if (branch == null || branch.Count == 0) return fallback;
            int index = branch.Count == 1 ? 0 : Math.Min(itemIndex, branch.Count - 1);
            double value = branch[index];
            return double.IsFinite(value) ? value : fallback;
        }

        private static double EstimateFlowAdjustedWidth(double nominalWidth, double height, double flow, double tol)
        {
            if (nominalWidth <= tol || height <= tol || flow <= tol ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = BeadArea(referenceWidth, height, tol);
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static double BeadArea(double width, double height, double tol)
        {
            if (width <= tol || height <= tol ||
                !double.IsFinite(width) || !double.IsFinite(height))
                return 0.0;

            double effectiveWidth = Math.Max(width, height * (1.0 - Math.PI / 4.0));
            double area = height * (effectiveWidth - height)
                + Math.PI * height * height / 4.0;
            return area > 0.0 && double.IsFinite(area) ? area : 0.0;
        }

        // ===================================================================
        //  SHARED GENERATOR HELPERS
        // ===================================================================

        private static class Gc
        {
            // ---- invariant formatting (parity with python "{:.2f}" etc.) ----
            public static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            public static string F5(double v) => v.ToString("0.00000", CultureInfo.InvariantCulture);
            public static string F0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
            public static string FG(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            /// <summary>python str(round(v,2)) look-alike: always at least one decimal.</summary>
            public static string Py2(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);

            /// <summary>Return val when it is a positive number, otherwise default.</summary>
            public static double NumOrDefault(double? val, double def)
            {
                if (!val.HasValue) return def;
                double v = val.Value;
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return def;
                return v;
            }

            /// <summary>Parse v as a positive float; return fallback otherwise.</summary>
            public static double SafeFloat(double v, double fallback)
            {
                return (!double.IsNaN(v) && !double.IsInfinity(v) && v > 0) ? v : fallback;
            }

            /// <summary>Prefix ';' when missing; null for blank input.</summary>
            public static string NormalizeComment(string text)
            {
                if (text == null) return null;
                string s = text.Trim();
                if (s.Length == 0) return null;
                return s.StartsWith(";") ? s : ";" + s;
            }

            public static List<string> ParseSampleInfo(List<string> raw)
            {
                var result = new List<string>();
                if (raw == null) return result;
                foreach (var item in raw)
                {
                    var line = NormalizeComment(item);
                    if (line != null) result.Add(line);
                }
                return result;
            }

            public static DataTree<string> ListToDataTree(List<List<string>> linesPerFile)
            {
                var tree = new DataTree<string>();
                for (int i = 0; i < linesPerFile.Count; i++)
                {
                    var path = new GH_Path(i);
                    foreach (var line in linesPerFile[i])
                        tree.Add(line, path);
                }
                return tree;
            }

            public static int LogicalLayerIndex(GH_Path path, int layerPathIndex)
            {
                if (path == null || path.Length == 0) return 0;
                int index = Math.Max(0, Math.Min(layerPathIndex, path.Length - 1));
                return path.Indices[index];
            }

            public static int LogicalCurveIndex(GH_Path path, int layerPathIndex, int fallbackIndex)
            {
                if (path == null || path.Length == 0) return fallbackIndex;
                int index = layerPathIndex + 1;
                return index >= 0 && index < path.Length ? path.Indices[index] : fallbackIndex;
            }

            /// <summary>
            /// Parse a DataTree of positive doubles.
            /// Returns (scalar, tree, repr): scalar when exactly one value, the tree
            /// when multiple, and the mean of all values as representative.
            /// Appends "'{name}' tree must contain only positive numbers." on failure.
            /// </summary>
            public static (double? scalar, DataTree<double> tree, double? repr)
                ParseNumberTree(DataTree<double> dataTree, string paramName, List<string> errors)
            {
                int total = 0;
                double totalSum = 0.0;
                bool bad = false;

                foreach (var branch in dataTree.Branches)
                {
                    foreach (var v in branch)
                    {
                        if (double.IsNaN(v) || double.IsInfinity(v)) { bad = true; continue; }
                        if (v <= 0) { bad = true; }
                        else { total++; totalSum += v; }
                    }
                }

                if (total == 0 || bad)
                {
                    errors.Add($"'{paramName}' tree must contain only positive numbers.");
                    return (null, null, null);
                }

                double reprVal = totalSum / total;

                if (total == 1)
                    return (totalSum, null, totalSum);
                return (null, dataTree, reprVal);
            }

            /// <summary>
            /// Appends Pr01 print_KPI statistics to the printing information block
            /// and raises runtime messages for risky/non-printable points.
            /// </summary>
            public static void AppendKpiStats(
                DataTree<double> kpi, List<string> printingInfo, GH_Component owner)
            {
                if (kpi == null || kpi.BranchCount == 0) return;

                var vals = kpi.AllData().Where(v => !double.IsNaN(v)).ToList();
                if (vals.Count == 0) return;

                int below1 = vals.Count(v => v < 1.0);
                int zeros  = vals.Count(v => v <= 0.0);
                double min  = vals.Min();
                double mean = vals.Average();
                double pct  = 100.0 * below1 / vals.Count;

                printingInfo.AddRange(new[]
                {
                    ";",
                    ";--- PRINTABILITY (Pr01 print_KPI) ---",
                    $";KPI points     : {vals.Count}",
                    $";KPI min / mean : {F2(min)} / {F2(mean)}",
                    $";KPI < 1.0      : {below1} ({pct.ToString("0.0", CultureInfo.InvariantCulture)} %)",
                    $";KPI = 0 (fail) : {zeros}",
                });

                if (zeros > 0)
                {
                    owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"print_KPI reports {zeros} non-printable point(s) (KPI = 0). " +
                        "Check the Pr01 Printability Assessment before printing.");
                }
                else if (below1 > 0)
                {
                    owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"print_KPI: {below1} point(s) below 1.0 (increasing collapse risk).");
                }
            }
        }

        // ===================================================================
        //  LDM GCODE GENERATOR  (1:1 port of the GhPython LDM v1.0.4 class)
        // ===================================================================

        private sealed class LdmGcodeGenerator
        {
            private readonly wsp_Gc03_Marlin_Gcode_From_Points _owner;
            private readonly string _versionTag;

            private readonly DataTree<Point3d> _printingPoints;
            private readonly DataTree<double>  _fluxes;
            private readonly DataTree<double>  _layerHInput;
            private readonly DataTree<double>  _printVolInput;
            private readonly DataTree<int> _strokeIds;
            private readonly double? _nozzleDiameterIn;

            private readonly double _fillamentMulti;
            private readonly double _travelSpeed;
            private readonly double _zHopSpeed;
            private readonly double _density;
            private readonly bool   _splitGcode;
            private double _volTresholdL;
            private readonly double _timeCorrection;
            private readonly List<string> _sampleInfo;

            private readonly double? _layerWInput;
            private readonly DataTree<double> _printingSpeedInput;
            private readonly DataTree<double> _printKpi;

            private double _nozzleDiameter;
            private double _layerW;
            private double? _zHop;
            private double ZHopV => _zHop ?? 0.0;

            private double? _layerHScalar;
            private DataTree<double> _layerHTree;
            private double _layerHRepr;

            private double? _psScalar;
            private DataTree<double> _psTree;
            private double _psRepr;

            private const string EMode = "M83 ;Relative";
            private const bool EAbsolute = false;

            private double _totalVolumeMm3, _printLenMm, _travelLenMm;
            private double _zhopUpMm, _zhopDownMm, _printTimeMinAcc;
            private double _minPrintF, _maxPrintF;
            private HashSet<int> _layersSet;
            private List<LayerBlock> _layerBlocks;

            private LayerBlock _currentLayerData;
            private Point3d? _prevEndPt;
            private GH_Path _prevPath;
            private bool _hoppedAfterPrevCurve;

            public DataTree<string> OutputGcodeTree { get; private set; } = new DataTree<string>();
            public DataTree<double> SpeedOutTree    { get; private set; } = new DataTree<double>();
            private List<string> _printingInfo = new List<string>();
            public double PTimeMin    { get; private set; }
            public double PPathLenMm  { get; private set; }
            public double PVolumeCm3  { get; private set; }
            public int    Layers      { get; private set; }

            private sealed class LayerBlock
            {
                public int LayerIndex;
                public Point3d FirstPt;
                public List<string> Lines = new List<string>();
                public double VolumeMm3;
            }

            // ------------------------------------------------------------------
            // 1. INITIALISATION
            // ------------------------------------------------------------------

            public LdmGcodeGenerator(
                wsp_Gc03_Marlin_Gcode_From_Points owner, string versionTag,
                DataTree<Point3d> printingPoints,
                DataTree<double> fluxes,
                DataTree<double> layerH,
                Wasper3dpParams prm,
                double? density,
                List<string> sampleInfo,
                DataTree<double> printKpi,
                DataTree<double> printVol,
                DataTree<int> strokeIds)
            {
                _owner = owner;
                _versionTag = versionTag;

                _printingPoints   = printingPoints;
                _fluxes           = fluxes;
                _layerHInput      = layerH;
                _printVolInput     = printVol;
                _strokeIds         = strokeIds;
                _nozzleDiameterIn = prm.NozzleDiameter;

                _fillamentMulti = Gc.NumOrDefault(prm.FillamentMulti, 5.15);
                _travelSpeed    = Gc.NumOrDefault(prm.TravelSpeed,    8000.0);
                _zHopSpeed      = Gc.NumOrDefault(prm.ZHopSpeed,      6000.0);
                _density        = Gc.NumOrDefault(density,            1600.0);
                _splitGcode     = prm.SplitGcode ?? false;
                _volTresholdL   = prm.SplitVolL ?? 4.5;   // documented default; warning fixes <=0 below
                _timeCorrection = Gc.NumOrDefault(prm.TimeCorrection, 1.75);
                _sampleInfo     = Gc.ParseSampleInfo(sampleInfo);

                _layerWInput        = prm.LayerW;
                _printingSpeedInput = prm.HasPrintSpeed ? prm.PrintSpeed : null;
                _printKpi           = printKpi;
                _zHop               = prm.ZHop;

                ResetAccumulators();
            }

            private void ResetAccumulators()
            {
                _totalVolumeMm3 = 0.0;
                _printLenMm     = 0.0;
                _travelLenMm    = 0.0;
                _zhopUpMm       = 0.0;
                _zhopDownMm     = 0.0;
                _printTimeMinAcc = 0.0;
                _minPrintF = double.PositiveInfinity;
                _maxPrintF = 0.0;
                _layersSet   = new HashSet<int>();
                _layerBlocks = new List<LayerBlock>();
            }

            // ------------------------------------------------------------------
            // 2. VALIDATION
            // ------------------------------------------------------------------

            public bool Validate()
            {
                var errors = new List<string>();

                if (_printingPoints == null || _printingPoints.BranchCount == 0)
                    errors.Add("'pt_planes' is missing or empty in wsp_path. Supply a valid WASPer Print Path; plane origins define the path.");

                if (_fluxes == null || _fluxes.BranchCount == 0)
                    errors.Add("'flows' is missing or empty in wsp_path. Supply a path containing point flows.");

                if (_layerHInput == null || _layerHInput.BranchCount == 0)
                    errors.Add("'layer_h' is missing in wsp_path. Supply a path containing positive layer heights.");
                else
                    ParseLayerH(errors);

                if (!_nozzleDiameterIn.HasValue || _nozzleDiameterIn.Value <= 0)
                    errors.Add("'nozzle_diameter' must be a positive number in mm — set it in the 3dp_params object (wsp_Gc01_LDM 3DP Parameters).");
                else
                    _nozzleDiameter = _nozzleDiameterIn.Value;

                if (_volTresholdL <= 0)
                {
                    Warn("'split_vol_L' must be > 0. Falling back to 4.5 L.");
                    _volTresholdL = 4.5;
                }

                if (_zHopSpeed < 60)
                {
                    Warn($"'z_hop_speed' is very low ({Gc.FG(_zHopSpeed)} mm/min). " +
                         "Feedrates are in mm/min, not mm/s.");
                }

                if (errors.Count > 0)
                {
                    foreach (var msg in errors)
                        _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                    return false;
                }

                _layerW = Gc.NumOrDefault(_layerWInput, _layerHRepr * 2.5);
                ParsePrintingSpeed();

                // z_hop defaults to zero/off. A value > 0 enables hop moves.
                if (!_zHop.HasValue)
                    _zHop = 0.0;

                if (ZHopV == 0.0)
                    Remark("z_hop = 0: no Z-hop moves will be generated. Input a value > 0 in 3dp_params to enable positive-Z hops.");

                return true;
            }

            private void ParseLayerH(List<string> errors)
            {
                var (scalar, tree, repr) = Gc.ParseNumberTree(_layerHInput, "layer_h", errors);
                _layerHScalar = scalar;
                _layerHTree   = tree;
                _layerHRepr   = repr ?? scalar ?? 0.0;
            }

            private void ParsePrintingSpeed()
            {
                const double def = 7000.0;

                if (_printingSpeedInput == null || _printingSpeedInput.BranchCount == 0)
                {
                    _psScalar = def;
                    _psRepr   = def;
                    return;
                }

                var errorsTmp = new List<string>();
                var (scalar, tree, repr) = Gc.ParseNumberTree(_printingSpeedInput, "printing_speed", errorsTmp);
                if (errorsTmp.Count > 0)
                {
                    Warn($"'printing_speed' tree has invalid values; falling back to {Gc.FG(def)} mm/min.");
                    _psScalar = def;
                    _psRepr   = def;
                }
                else
                {
                    _psScalar = scalar;
                    _psTree   = tree;
                    _psRepr   = repr ?? scalar ?? def;
                }
            }

            // ------------------------------------------------------------------
            // 3. MAIN RUN
            // ------------------------------------------------------------------

            public void Run()
            {
                ResetAccumulators();
                SpeedOutTree = new DataTree<double>();
                _printingInfo = BuildPrintingInfoHeader();
                CollectAllLayerBlocks();
                FinaliseStatistics();
                Gc.AppendKpiStats(_printKpi, _printingInfo, _owner);
                var fileGroups = SplitIntoFileGroups();
                var allFiles = AssembleGcodeFiles(fileGroups);
                OutputGcodeTree = Gc.ListToDataTree(allFiles);
            }

            // ------------------------------------------------------------------
            // 4. PRINTING INFO HEADER
            // ------------------------------------------------------------------

            private List<string> BuildPrintingInfoHeader()
            {
                return new List<string>
                {
                    ";----PRINTING INFORMATION:----",
                    $";Plugin: WASPer_3DP - Generator: wsp_Gc03 LDM {_versionTag}",
                    $";Nozzle diameter: {Gc.F2(_nozzleDiameter)} mm",
                    $";Filament multiplier diameter: {Gc.F2(_fillamentMulti)} mm",
                    $";Layer height (repr.): {Gc.F2(_layerHRepr)} mm",
                    $";Extrusion mode: {(EAbsolute ? "Absolute" : "Relative")}",
                    $";Travel speed: {Gc.F0(_travelSpeed)} mm/min",
                    $";Z-hop: {Gc.F2(ZHopV)} mm @ {Gc.F0(_zHopSpeed)} mm/min",
                    $";Volume split: {(_splitGcode ? "ON" : "OFF")}",
                    $";Volume threshold: {Gc.F2(_volTresholdL)} L",
                    $";Sample info lines: {_sampleInfo.Count}",
                    $";Time correction factor: {Gc.F2(_timeCorrection)}",
                };
            }

            // ------------------------------------------------------------------
            // 5. MAIN LOOP
            // ------------------------------------------------------------------

            private void CollectAllLayerBlocks()
            {
                var paths = _printingPoints.Paths.ToList();
                int nPaths = paths.Count;
                int layerPathIndex = WasperGcodeTreeUtil.CommonPathPrefixLength(paths);

                _currentLayerData      = null;
                _prevEndPt             = null;
                _hoppedAfterPrevCurve  = false;

                for (int branchIndex = 0; branchIndex < nPaths; branchIndex++)
                {
                    var path = paths[branchIndex];
                    var pts = _printingPoints.Branch(path);
                    if (pts == null || pts.Count == 0) continue;

                    var fluxVals = GetFluxVals(path, pts.Count);
                    if (fluxVals.Count != pts.Count) continue;

                    var layerHVals = GetLayerHVals(path, pts.Count);
                    var printVolVals = GetPrintVolVals(path, pts.Count);
                    var speedVals  = GetSpeedVals(path, pts.Count);

                    SpeedOutTree.AddRange(speedVals, path);

                    ProcessOneBranch(path, pts, fluxVals, layerHVals, printVolVals, speedVals,
                                     branchIndex, nPaths, paths, layerPathIndex);
                }

                if (_currentLayerData != null)
                    _layerBlocks.Add(_currentLayerData);
            }

            private List<double> GetFluxVals(GH_Path path, int n)
            {
                if (_fluxes != null && _fluxes.PathExists(path) && _fluxes.Branch(path).Count == n)
                    return new List<double>(_fluxes.Branch(path));
                return Enumerable.Repeat(1.0, n).ToList();
            }

            private List<double> GetLayerHVals(GH_Path path, int n)
            {
                double fallback = _layerHScalar ?? _layerHRepr;
                return ResolveBranchFloats(path, n, _layerHTree, fallback, "layer_h");
            }

            private List<double> GetPrintVolVals(GH_Path path, int n)
            {
                return ResolveBranchFloats(path, n, _printVolInput, 0.0, "print_vol");
            }

            private List<double> GetSpeedVals(GH_Path path, int n)
            {
                double fallback = _psScalar ?? _psRepr;
                return ResolveBranchFloats(path, n, _psTree, fallback, "printing_speed");
            }

            /// <summary>
            /// 1 value ? broadcast; n values ? as-is; other length ? warn + broadcast
            /// first value; missing branch ? broadcast fallback.
            /// </summary>
            private List<double> ResolveBranchFloats(
                GH_Path path, int nPoints, DataTree<double> dataTree,
                double fallback, string paramName)
            {
                if (dataTree == null || !dataTree.PathExists(path))
                    return Enumerable.Repeat(fallback, nPoints).ToList();

                var raw = dataTree.Branch(path);
                var parsed = raw.Select(v => Gc.SafeFloat(v, fallback)).ToList();

                if (parsed.Count == 1)
                    return Enumerable.Repeat(parsed[0], nPoints).ToList();

                if (parsed.Count == nPoints)
                    return parsed;

                Warn($"Branch {path} of '{paramName}' has {parsed.Count} values but {nPoints} points. " +
                     "Using its first value for the whole curve.");
                return Enumerable.Repeat(parsed[0], nPoints).ToList();
            }

            // ------------------------------------------------------------------
            // 6. BRANCH PROCESSING
            // ------------------------------------------------------------------

            private void ProcessOneBranch(
                GH_Path path, IList<Point3d> pts,
                List<double> fluxVals, List<double> layerHVals, List<double> printVolVals, List<double> speedVals,
                int branchIndex, int nPaths, List<GH_Path> paths, int layerPathIndex)
            {
                int layerIndex = Gc.LogicalLayerIndex(path, layerPathIndex);
                int curveIndex = Gc.LogicalCurveIndex(path, layerPathIndex, branchIndex);
                Point3d startPt = pts[0];

                if (_currentLayerData == null || _currentLayerData.LayerIndex != layerIndex)
                    CloseAndOpenLayer(layerIndex, startPt);

                bool continuousFromPrevious = IsContinuousJoin(_prevPath, _prevEndPt, path, startPt);
                if (!continuousFromPrevious)
                    EmitTravelToCurveStart(startPt);
                else
                    AddLine($"; Continuous stroke {WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, path)} — no travel or Z-hop");
                _hoppedAfterPrevCurve = false;

                // Curve start anchor — resets E to 0
                double fFirst = speedVals[0];
                AddLine($"; --- Curve {curveIndex + 1} / Layer {layerIndex + 1}");
                AddLine($"G1 F{Gc.FG(fFirst)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(startPt.Z)} E0.00  ; curve start");
                TrackSpeed(fFirst);

                // Printing moves — one G1 line per segment
                Point3d prevPt = startPt;
                for (int i = 1; i < pts.Count; i++)
                {
                    prevPt = EmitPrintMove(pts[i], prevPt,
                        fluxVals[i], layerHVals[i], speedVals[i], printVolVals[i]);
                }

                AddLine($"; --- End of Curve {curveIndex + 1} / Layer {layerIndex + 1}");

                // End-of-layer Z-hop
                bool isLastCurveInLayer =
                    branchIndex == nPaths - 1 ||
                    Gc.LogicalLayerIndex(paths[branchIndex + 1], layerPathIndex) != layerIndex;

                bool continuesToNext = false;
                if (branchIndex + 1 < nPaths)
                {
                    GH_Path nextPath = paths[branchIndex + 1];
                    IList<Point3d> nextPoints = _printingPoints.Branch(nextPath);
                    continuesToNext = nextPoints != null && nextPoints.Count > 0 &&
                        IsContinuousJoin(path, prevPt, nextPath, nextPoints[0]);
                }

                if (ZHopV > 0 && isLastCurveInLayer && !continuesToNext)
                {
                    EmitEndOfLayerZhop(prevPt);
                    _hoppedAfterPrevCurve = true;
                }

                _prevEndPt = prevPt;
                _prevPath = path;
            }

            private bool IsContinuousJoin(
                GH_Path previousPath,
                Point3d? previousEnd,
                GH_Path currentPath,
                Point3d currentStart)
            {
                if (previousPath == null || !previousEnd.HasValue)
                    return false;
                int previousStroke = WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, previousPath);
                int currentStroke = WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, currentPath);
                double tolerance = Rhino.RhinoMath.ZeroTolerance;
                return previousStroke >= 0 && previousStroke == currentStroke &&
                    previousEnd.Value.DistanceToSquared(currentStart) <= tolerance * tolerance;
            }

            private bool IsContinuousJoin(
                GH_Path previousPath,
                Point3d previousEnd,
                GH_Path currentPath,
                Point3d currentStart) =>
                IsContinuousJoin(previousPath, (Point3d?)previousEnd, currentPath, currentStart);

            private void CloseAndOpenLayer(int layerIndex, Point3d firstPt)
            {
                if (_currentLayerData != null)
                    _layerBlocks.Add(_currentLayerData);

                _layersSet.Add(layerIndex);

                _currentLayerData = new LayerBlock
                {
                    LayerIndex = layerIndex,
                    FirstPt    = firstPt,
                };
                AddLine($"; ===== Layer {layerIndex + 1} | height (repr.) {Gc.F2(_layerHRepr)} mm =====");
            }

            private void AddLine(string line) => _currentLayerData.Lines.Add(line);

            private void TrackSpeed(double f)
            {
                if (f > 0)
                {
                    if (f < _minPrintF) _minPrintF = f;
                    if (f > _maxPrintF) _maxPrintF = f;
                }
            }

            // ------------------------------------------------------------------
            // 7. TRAVEL MOVE EMITTERS
            // ------------------------------------------------------------------

            private void EmitTravelToCurveStart(Point3d startPt)
            {
                if (!_prevEndPt.HasValue)
                    EmitFirstTravel(startPt);
                else
                    EmitInterCurveTravel(startPt);
            }

            private void EmitFirstTravel(Point3d startPt)
            {
                if (ZHopV > 0)
                {
                    double safeZ = startPt.Z + ZHopV;
                    AddLine($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(safeZ)}  ; move above first point");
                    AddLine($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(startPt.Z)}  ; descend to start");
                    _zhopDownMm += ZHopV;
                }
                else
                {
                    AddLine($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(startPt.Z)}  ; move to first point");
                }
            }

            private void EmitInterCurveTravel(Point3d startPt)
            {
                Point3d prev = _prevEndPt.Value;

                if (ZHopV > 0 && !_hoppedAfterPrevCurve)
                {
                    double targetHopZ = Math.Max(prev.Z, startPt.Z) + ZHopV;
                    AddLine($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(targetHopZ)}  ; Z-hop up (+world Z)");
                    _zhopUpMm += Math.Max(0.0, targetHopZ - prev.Z);
                }

                double xyDist = Math.Sqrt(
                    (startPt.X - prev.X) * (startPt.X - prev.X) +
                    (startPt.Y - prev.Y) * (startPt.Y - prev.Y));
                AddLine($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)}  ; travel to next curve");
                _travelLenMm += xyDist;

                if (ZHopV > 0)
                {
                    AddLine($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(startPt.Z)}  ; Z-hop down");
                    _zhopDownMm += Math.Max(0.0, Math.Max(prev.Z, startPt.Z) + ZHopV - startPt.Z);
                }
            }

            private void EmitEndOfLayerZhop(Point3d endPt)
            {
                AddLine($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(endPt.Z + ZHopV)}  ; Z-hop end of layer");
                _zhopUpMm += ZHopV;
            }

            // ------------------------------------------------------------------
            // 8. PRINTING MOVE EMITTER
            // ------------------------------------------------------------------

            private Point3d EmitPrintMove(Point3d pt, Point3d prevPt,
                double flux, double layerH, double speed, double packedVolume)
            {
                double d = prevPt.DistanceTo(pt);
                double fLocal = speed > 0 ? speed : _psRepr;

                // Volume: effective cross-section × length.
                // Keep the established G-code extrusion model. packedVolume is
                // retained for future comparison but does not alter E in this phase.
                double segVol = (_layerW * flux) * layerH * d;
                _totalVolumeMm3 += segVol;
                _currentLayerData.VolumeMm3 += segVol;

                // Extrusion: E = flux × (nozzle_d × layer_h × d) / (p × (filament_d/2)²)
                double filamentArea = Math.PI * Math.Pow(_fillamentMulti / 2.0, 2);
                double extr = flux * (_nozzleDiameter * layerH * d) / filamentArea;

                AddLine($"G1 F{Gc.FG(fLocal)} X{Gc.F2(pt.X)} Y{Gc.F2(pt.Y)} Z{Gc.F2(pt.Z)} E{Gc.F2(extr)}");

                _printLenMm += d;
                if (fLocal > 0)
                    _printTimeMinAcc += d / fLocal;

                TrackSpeed(fLocal);
                return pt;
            }

            // ------------------------------------------------------------------
            // 9. STATISTICS & SUMMARY
            // ------------------------------------------------------------------

            private void FinaliseStatistics()
            {
                double travelTimeMin = _travelSpeed > 0 ? _travelLenMm / _travelSpeed : 0.0;

                double zhopDistMm  = _zhopUpMm + _zhopDownMm;
                double zhopTimeMin = _zHopSpeed > 0 ? zhopDistMm / _zHopSpeed : 0.0;

                double rawTimeMin = _printTimeMinAcc + travelTimeMin + zhopTimeMin;

                double totalTimeMin = rawTimeMin * _timeCorrection;

                Layers     = _layersSet.Count;
                PTimeMin   = Math.Round(totalTimeMin, 2);
                PPathLenMm = Math.Round(_printLenMm, 2);
                PVolumeCm3 = Math.Round(_totalVolumeMm3 / 1000.0, 2);

                double volumeL  = _totalVolumeMm3 / 1e6;
                double volumeM3 = _totalVolumeMm3 / 1e9;
                double massKg   = Math.Round(_density * volumeM3, 2);
                double pTimeH   = Math.Round(PTimeMin / 60.0, 2);

                string speedLine;
                if (double.IsPositiveInfinity(_minPrintF))
                    speedLine = $";Print speed: {Gc.F0(_psRepr)} mm/min";
                else if (Math.Abs(_maxPrintF - _minPrintF) < 0.5)
                    speedLine = $";Print speed: {Gc.F0(_minPrintF)} mm/min";
                else
                    speedLine = $";Print speed: {Gc.F0(_minPrintF)} - {Gc.F0(_maxPrintF)} mm/min";

                _printingInfo.Add(speedLine);

                _printingInfo.AddRange(new[]
                {
                    ";",
                    ";--- SUMMARY ---",
                    $";Printed volume : {Gc.F2(PVolumeCm3)} cm3  |  {Gc.F2(volumeL)} L",
                    $";Estimated mass : {Gc.F2(massKg)} kg  (density {Gc.FG(_density)} kg/m3)",
                    $";Raw time       : {Gc.F2(rawTimeMin)} min",
                    $";x correction   : {Gc.F2(_timeCorrection)}",
                    $";Total time     : {Gc.F2(PTimeMin)} min  |  {Gc.F2(pTimeH)} h",
                    $";Print path     : {Gc.F2(_printLenMm)} mm",
                    $";Travel (G0 XY) : {Gc.F2(_travelLenMm)} mm",
                    $";Z-hop up/down  : {Gc.F2(_zhopUpMm)} / {Gc.F2(_zhopDownMm)} mm",
                });
            }

            // ------------------------------------------------------------------
            // 10. FILE SPLITTING
            // ------------------------------------------------------------------

            private List<List<LayerBlock>> SplitIntoFileGroups()
            {
                if (_layerBlocks.Count == 0)
                    return new List<List<LayerBlock>> { new List<LayerBlock>() };

                if (!_splitGcode)
                    return new List<List<LayerBlock>> { _layerBlocks };

                double thresholdMm3 = _volTresholdL * 1e6;
                var groups = new List<List<LayerBlock>>();
                var currentGroup = new List<LayerBlock>();
                double currentGroupVol = 0.0;

                foreach (var lb in _layerBlocks)
                {
                    double lbVol = lb.VolumeMm3;

                    if (lbVol > thresholdMm3)
                    {
                        Warn($"Layer {lb.LayerIndex + 1} volume ({Gc.F2(lbVol / 1e6)} L) exceeds the " +
                             $"threshold ({Gc.F2(_volTresholdL)} L). It will be its own G-code file.");
                    }

                    if (currentGroup.Count == 0)
                    {
                        currentGroup.Add(lb);
                        currentGroupVol = lbVol;
                    }
                    else if (currentGroupVol + lbVol > thresholdMm3)
                    {
                        groups.Add(currentGroup);
                        currentGroup = new List<LayerBlock> { lb };
                        currentGroupVol = lbVol;
                    }
                    else
                    {
                        currentGroup.Add(lb);
                        currentGroupVol += lbVol;
                    }
                }

                if (currentGroup.Count > 0)
                    groups.Add(currentGroup);

                return groups;
            }

            // ------------------------------------------------------------------
            // 11. FILE ASSEMBLY
            // ------------------------------------------------------------------

            private List<List<string>> AssembleGcodeFiles(List<List<LayerBlock>> fileGroups)
            {
                var endBlock = BuildEndGcode();
                int fileCount = fileGroups.Count;
                var allFiles = new List<List<string>>();

                for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
                {
                    var group = fileGroups[fileIndex];

                    Point3d firstPt;
                    int layerStartIdx, layerEndIdx;
                    double localVolMm3;

                    if (group.Count > 0)
                    {
                        firstPt       = group[0].FirstPt;
                        layerStartIdx = group[0].LayerIndex;
                        layerEndIdx   = group[group.Count - 1].LayerIndex;
                        localVolMm3   = group.Sum(lb => lb.VolumeMm3);
                    }
                    else
                    {
                        var firstBranch = _printingPoints.Branches.FirstOrDefault(b => b != null && b.Count > 0);
                        firstPt       = firstBranch != null ? firstBranch[0] : new Point3d(0, 0, 0);
                        layerStartIdx = 0;
                        layerEndIdx   = 0;
                        localVolMm3   = 0.0;
                    }

                    var header = BuildFileHeader(fileIndex, fileCount,
                        layerStartIdx, layerEndIdx, localVolMm3);
                    var startBlock = BuildStartGcode(firstPt);

                    var fileLines = new List<string>(header);
                    fileLines.AddRange(startBlock);
                    foreach (var lb in group)
                        fileLines.AddRange(lb.Lines);
                    fileLines.AddRange(endBlock);

                    allFiles.Add(fileLines);
                }

                return allFiles;
            }

            private List<string> BuildFileHeader(
                int fileIndex, int fileCount,
                int layerStartIdx, int layerEndIdx,
                double localVolMm3)
            {
                var header = new List<string>
                {
                    $";---- GCODE FILE {fileIndex + 1} OF {fileCount} ----"
                };

                if (_sampleInfo.Count > 0)
                    header.AddRange(_sampleInfo);

                if (_splitGcode)
                {
                    header.AddRange(new[]
                    {
                        ";Split mode: ON",
                        $";Layers in this file: {layerStartIdx + 1} to {layerEndIdx + 1}",
                        $";Volume this file : {Gc.F2(localVolMm3 / 1e6)} L",
                        $";Total print volume: {Gc.F2(_totalVolumeMm3 / 1e6)} L",
                    });
                }
                else
                {
                    header.Add(";Split mode: OFF");
                }

                header.AddRange(_printingInfo);
                return header;
            }

            private List<string> BuildStartGcode(Point3d firstPt)
            {
                double safeZ = firstPt.Z + Math.Max(2.0, ZHopV);
                return new List<string>
                {
                    "; -- START GCODE --",
                    "G90",
                    EMode,
                    "G28",
                    "G92 E0.00",
                    $"G0 F8000 Z{Gc.F2(safeZ)}  ; safe Z before first move",
                    "; -- END START GCODE --",
                };
            }

            private List<string> BuildEndGcode()
            {
                return new List<string>
                {
                    "; -- END GCODE --",
                    "G1 E-4.00 F6000  ; retract",
                    "G92 E0.00",
                    "G28",
                    "; -- END OF END GCODE --",
                };
            }

            private void Warn(string msg) =>
                _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);

            private void Remark(string msg) =>
                _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, msg);
        }

        // ===================================================================
        //  FDM GCODE GENERATOR  (1:1 port of the GhPython FDM v1.0.4 script)
        // ===================================================================

        private sealed class FdmGcodeGenerator
        {
            private readonly wsp_Gc03_Marlin_Gcode_From_Points _owner;
            private readonly string _versionTag;

            private readonly DataTree<Point3d> _printingPoints;
            private readonly DataTree<double>  _fluxes;
            private readonly DataTree<double>  _layerHInput;
            private readonly DataTree<double>  _printVolInput;
            private readonly DataTree<int> _strokeIds;
            private readonly double? _nozzleDiameterIn;

            private readonly double _filamentDiam;
            private readonly double _tempNozzle;
            private readonly double _tempBed;
            private readonly double _travelSpeed;
            private readonly double _zHopSpeed;
            private readonly double _density;
            private readonly List<string> _sampleInfo;

            private readonly double? _layerWInput;
            private readonly DataTree<double> _printingSpeedInput;
            private readonly DataTree<double> _fanSpeed;
            private readonly List<string> _customStart;
            private readonly List<string> _customEnd;
            private readonly DataTree<double> _printKpi;

            private double _nozzleDiameter;
            private double _layerW;
            private double? _zHop;
            private double ZHopV => _zHop ?? 0.0;

            private double? _layerHScalar;
            private DataTree<double> _layerHTree;
            private double _layerHRepr;

            private double? _psScalar;
            private DataTree<double> _psTree;
            private double _psRepr;

            private const string EMode = "M83 ; Relative extrusion";

            public DataTree<string> OutputGcodeTree { get; private set; } = new DataTree<string>();
            public DataTree<double> SpeedOutTree    { get; private set; } = new DataTree<double>();
            public double PTimeMin    { get; private set; }
            public double PPathLenMm  { get; private set; }
            public double PVolumeCm3  { get; private set; }
            public int    Layers      { get; private set; }

            public FdmGcodeGenerator(
                wsp_Gc03_Marlin_Gcode_From_Points owner, string versionTag,
                DataTree<Point3d> printingPoints,
                DataTree<double> fluxes,
                DataTree<double> layerH,
                Wasper3dpParams prm,
                double? density,
                List<string> sampleInfo,
                DataTree<double> printKpi,
                DataTree<double> printVol,
                DataTree<int> strokeIds)
            {
                _owner = owner;
                _versionTag = versionTag;

                _printingPoints   = printingPoints;
                _fluxes           = fluxes;
                _layerHInput      = layerH;
                _printVolInput     = printVol;
                _strokeIds         = strokeIds;
                _nozzleDiameterIn = prm.NozzleDiameter;

                _filamentDiam = Gc.NumOrDefault(prm.FillamentMulti, 1.75);
                _tempNozzle   = Gc.NumOrDefault(prm.TempNozzle,     200.0);
                _tempBed      = prm.TempBed ?? 60.0;   // python allow_zero=True: 0 is valid (bed off)
                _travelSpeed  = Gc.NumOrDefault(prm.TravelSpeed,    5000.0);
                _zHopSpeed    = Gc.NumOrDefault(prm.ZHopSpeed,      3000.0);
                _density      = Gc.NumOrDefault(density,            1240.0);   // PLA-ish
                _sampleInfo   = Gc.ParseSampleInfo(sampleInfo);

                _layerWInput        = prm.LayerW;
                _printingSpeedInput = prm.HasPrintSpeed ? prm.PrintSpeed : null;
                _fanSpeed           = prm.HasFanSpeed ? prm.FanSpeed : null;
                _customStart        = prm.CustomStartGcode;
                _customEnd          = prm.CustomEndGcode;
                _printKpi           = printKpi;
                _zHop               = prm.ZHop;
            }

            // ------------------------------------------------------------------
            //  VALIDATION (FDM python "HARD mandatory checks")
            // ------------------------------------------------------------------

            public bool Validate()
            {
                var errors = new List<string>();

                if (_printingPoints == null || _printingPoints.BranchCount == 0)
                    errors.Add("Mandatory path field 'pt_planes' is missing or empty in wsp_path; plane origins define the path.");

                if (_fluxes == null || _fluxes.BranchCount == 0)
                    errors.Add("Mandatory path field 'flows' is missing or empty in wsp_path.");

                if (_layerHInput == null || _layerHInput.BranchCount == 0)
                {
                    errors.Add("Mandatory path field 'layer_h' is missing in wsp_path.");
                }
                else
                {
                    var errTmp = new List<string>();
                    var (scalar, tree, repr) = Gc.ParseNumberTree(_layerHInput, "layer_h", errTmp);
                    if (errTmp.Count > 0)
                        errors.Add("Mandatory input " + errTmp[0]);
                    _layerHScalar = scalar;
                    _layerHTree   = tree;
                    _layerHRepr   = repr ?? scalar ?? 0.0;
                }

                if (!_nozzleDiameterIn.HasValue || _nozzleDiameterIn.Value <= 0)
                    errors.Add("Mandatory input 'nozzle_diameter' must be a positive number in mm — set it in the 3dp_params object (wsp_Gc02_FDM 3DP Parameters).");
                else
                    _nozzleDiameter = _nozzleDiameterIn.Value;

                if (errors.Count > 0)
                {
                    foreach (var msg in errors)
                        _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                    return false;
                }

                // FDM defaults dependent on validated inputs
                _layerW = Gc.NumOrDefault(_layerWInput, _layerHRepr * 2.5);
                ParsePrintingSpeed();

                // z_hop defaults to zero/off. A value > 0 enables hop moves.
                if (!_zHop.HasValue)
                    _zHop = 0.0;

                if (ZHopV == 0.0)
                    Remark("z_hop = 0: no Z-hop moves will be generated. Input a value > 0 in 3dp_params to enable positive-Z hops.");

                return true;
            }

            private void ParsePrintingSpeed()
            {
                const double def = 1200.0;   // FDM baseline

                if (_printingSpeedInput == null || _printingSpeedInput.BranchCount == 0)
                {
                    _psScalar = def;
                    _psRepr   = def;
                    return;
                }

                var errorsTmp = new List<string>();
                var (scalar, tree, repr) = Gc.ParseNumberTree(_printingSpeedInput, "printing_speed", errorsTmp);
                if (errorsTmp.Count > 0)
                {
                    Warn("'printing_speed' has invalid/non-positive values; falling back to default 1200 mm/min.");
                    _psScalar = def;
                    _psRepr   = def;
                }
                else
                {
                    _psScalar = scalar;
                    _psTree   = tree;
                    _psRepr   = repr ?? scalar ?? def;
                }
            }

            // ------------------------------------------------------------------
            //  FAN HELPERS (python _fan_to_S / _fan_for_layer)
            // ------------------------------------------------------------------

            private static int? FanToS(double v)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                double fv = v;
                if (fv < 0) fv = 0;
                if (fv <= 100.0) fv = fv * 255.0 / 100.0;   // percent-like ? Marlin S
                if (fv > 255) fv = 255;
                return (int)Math.Round(fv);
            }

            private int? FanForLayer(int layerIndex)
            {
                if (_fanSpeed == null) return null;

                var p = new GH_Path(layerIndex);
                if (_fanSpeed.PathExists(p))
                {
                    var br = _fanSpeed.Branch(p);
                    if (br != null && br.Count > 0) return FanToS(br[0]);
                }

                var p2 = new GH_Path(layerIndex, 0);
                if (_fanSpeed.PathExists(p2))
                {
                    var br = _fanSpeed.Branch(p2);
                    if (br != null && br.Count > 0) return FanToS(br[0]);
                }

                // single branch ? per-layer list semantics (last value repeats)
                if (_fanSpeed.BranchCount == 1)
                {
                    var br = _fanSpeed.Branches[0];
                    if (br == null || br.Count == 0) return null;
                    int idx = layerIndex < br.Count ? layerIndex : br.Count - 1;
                    return FanToS(br[idx]);
                }

                return null;
            }

            // ------------------------------------------------------------------
            //  MAIN RUN (mirrors the FDM python script flow)
            // ------------------------------------------------------------------

            public void Run()
            {
                SpeedOutTree = new DataTree<double>();

                // ---- Start/End blocks (defaults or custom overrides) ----------
                var defaultStart = new List<string>
                {
                    "; -- START GCODE --",
                    "G90 ; Absolute positioning",
                    EMode,
                    "M220 S100 ; Reset Feedrate",
                    "M221 S100 ; Reset Flowrate",
                    $"M140 S{Gc.F0(_tempBed)} ; Set bed temperature",
                    "M105 ; Report temperatures",
                    $"M190 S{Gc.F0(_tempBed)} ; Wait for bed temperature",
                    $"M104 S{Gc.F0(_tempNozzle)} ; Set nozzle temperature",
                    "G28 ; Home",
                    "M420 S1 ; Enable mesh leveling",
                    "M413 S0 ; Power Loss Off",
                    "G92 E0 ; Reset Extruder",
                    "G1 Z2.0 F3000 ; Move Z Axis up",
                    "G1 X-2.1 Y20 Z0.28 F5000.0 ; Move to start position",
                    $"M109 S{Gc.F0(_tempNozzle)} ; Wait for nozzle temperature",
                    "G1 X-2.1 Y145.0 Z0.28 F1500.0 E15 ; Draw the first line",
                    "G1 X-2.4 Y145.0 Z0.28 F5000.0 ; Move to side a little",
                    "G1 X-2.4 Y20 Z0.28 F1500.0 E30 ; Draw the second line",
                    "G92 E0 ; Reset Extruder",
                    "G1 E-3.0000 F1800 ; Retract a bit",
                    "G1 Z2.0 F3000 ; Move Z Axis up",
                    "G1 E0.0000 F1800",
                    "G92 E0",
                    "G1 F6000 E-5",
                    "; -- END OF START GCODE --",
                };

                var defaultEnd = new List<string>
                {
                    "; -- END GCODE --",
                    "M104 S0 ; Turn off nozzle heater",
                    "M140 S0 ; Turn off bed heater",
                    "M107 ; Turn off fan",
                    "G91 ; Relative positioning",
                    "G1 E-1 F300 ; Retract the filament slightly",
                    "G1 Z+0.5 E-5 X-20 Y-20 F3000 ; Move Z up and retract more",
                    "G28 X0 Y0 ; Move X/Y to min endstops",
                    "M84 ; Disable motors",
                    "; -- END OF END GCODE --",
                };

                var startGcode = (_customStart != null && _customStart.Count > 0) ? _customStart : defaultStart;
                var endGcode   = (_customEnd   != null && _customEnd.Count   > 0) ? _customEnd   : defaultEnd;

                // ---- Header info ----------------------------------------------
                var printingInfo = new List<string>
                {
                    ";PRINTING INFORMATION:",
                    $";Generator: wsp_Gc03 FDM {_versionTag}",
                    $";Nozzle diameter: {Gc.F2(_nozzleDiameter)} [mm]",
                    $";Layer height (repr.): {Gc.F2(_layerHRepr)} [mm]",
                    $";Filament diameter: {Gc.F2(_filamentDiam)} [mm]",
                    $";Line width (for volume): {Gc.F2(_layerW)} [mm]",
                    $";Travel speed (G0 F): {Gc.F0(_travelSpeed)} [mm/min]",
                    $";Z-hop: {Gc.F2(ZHopV)} [mm] @ {Gc.F0(_zHopSpeed)} [mm/min]",
                    "; Extrusion per segment:",
                    ";  d = dist(P_{i-1}, P_i) [mm]",
                    ";  A_fil = pi*(filament_diam/2)^2 [mm^2]",
                    ";  V_seg = flux_i * layer_w * layer_h_local * d [mm^3]",
                    ";  E_seg = V_seg / A_fil [mm of filament] (RELATIVE E via M83)",
                };

                // sample_info (addition vs the FDM python, which had no such input)
                if (_sampleInfo.Count > 0)
                    printingInfo.AddRange(_sampleInfo);

                // ---- Main loop --------------------------------------------------
                var gCodePre = new List<string>();

                double printLenMm = 0.0, travelLenMm = 0.0;
                double zhopUpMm = 0.0, zhopDownMm = 0.0;
                double totalVolumeMm3 = 0.0;
                var layersSet = new HashSet<int>();
                double printTimeMinAcc = 0.0;

                double minPrintF = double.PositiveInfinity;
                double maxPrintF = 0.0;

                Point3d? prevEndPt = null;
                GH_Path prevPath = null;
                bool hoppedAfterPrevCurve = false;

                var paths = _printingPoints.Paths.ToList();
                int nPaths = paths.Count;
                int layerPathIndex = WasperGcodeTreeUtil.CommonPathPrefixLength(paths);

                double aFil = Math.PI * Math.Pow(_filamentDiam * 0.5, 2);

                for (int branchIndex = 0; branchIndex < nPaths; branchIndex++)
                {
                    var path = paths[branchIndex];
                    int layerIndex = Gc.LogicalLayerIndex(path, layerPathIndex);
                    int curveIndex = Gc.LogicalCurveIndex(path, layerPathIndex, branchIndex);
                    var pts = _printingPoints.Branch(path);

                    // Flux values (must match pts length, else fallback to 1.0)
                    List<double> fluxVals = null;
                    if (_fluxes != null && _fluxes.PathExists(path))
                    {
                        var tmp = _fluxes.Branch(path);
                        if (tmp != null && pts != null && tmp.Count == pts.Count)
                            fluxVals = new List<double>(tmp);
                    }
                    if (fluxVals == null && pts != null)
                        fluxVals = Enumerable.Repeat(1.0, pts.Count).ToList();

                    if (pts == null || pts.Count < 2)
                        continue;

                    // Layer height values (FDM rules: invalid entries ? repr)
                    var layerHVals = ResolveFdmBranch(path, pts.Count, _layerHTree,
                        _layerHScalar, _layerHRepr, "layer_h");
                    var printVolVals = ResolveFdmBranch(path, pts.Count, _printVolInput,
                        0.0, 0.0, "print_vol");

                    // Printing speed values
                    var speedVals = ResolveFdmBranch(path, pts.Count, _psTree,
                        _psScalar, _psRepr, "printing_speed");

                    SpeedOutTree.AddRange(speedVals, path);

                    // New layer marker + fan for layer
                    if (!layersSet.Contains(layerIndex))
                    {
                        layersSet.Add(layerIndex);
                        gCodePre.Add($"; ***** START of layer {layerIndex + 1} *****");

                        int? fanS = FanForLayer(layerIndex);
                        if (fanS.HasValue)
                            gCodePre.Add($"M106 S{fanS.Value} ; Fan speed for layer {layerIndex + 1}");
                    }

                    // Inter-curve travel
                    Point3d startPt = pts[0];

                    bool continuousFromPrevious =
                        prevPath != null && prevEndPt.HasValue &&
                        IsContinuousJoin(prevPath, prevEndPt.Value, path, startPt);

                    if (prevEndPt.HasValue && !continuousFromPrevious)
                    {
                        Point3d prev = prevEndPt.Value;

                        if (ZHopV > 0 && !hoppedAfterPrevCurve)
                        {
                            double targetUpZ = Math.Max(prev.Z, startPt.Z) + ZHopV;
                            gCodePre.Add($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(targetUpZ)} ; Z-hop up (+world Z)");
                            zhopUpMm += Math.Max(0.0, targetUpZ - prev.Z);
                        }

                        gCodePre.Add($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} ; Travel XY");
                        travelLenMm += Math.Sqrt(
                            (startPt.X - prev.X) * (startPt.X - prev.X) +
                            (startPt.Y - prev.Y) * (startPt.Y - prev.Y));

                        if (ZHopV > 0)
                        {
                            gCodePre.Add($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(startPt.Z)} ; Z-hop down");
                            zhopDownMm += Math.Max(0.0, Math.Max(prev.Z, startPt.Z) + ZHopV - startPt.Z);
                        }

                        hoppedAfterPrevCurve = false;
                    }
                    else if (!prevEndPt.HasValue)
                    {
                        // first curve travel
                        if (ZHopV > 0)
                        {
                            gCodePre.Add($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(startPt.Z + ZHopV)}");
                            gCodePre.Add($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(startPt.Z)}");
                            zhopDownMm += ZHopV;
                        }
                        else
                        {
                            gCodePre.Add($"G0 F{Gc.FG(_travelSpeed)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(startPt.Z)}");
                        }
                    }
                    else
                    {
                        gCodePre.Add($"; Continuous stroke {WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, path)} — no travel or Z-hop");
                        hoppedAfterPrevCurve = false;
                    }

                    gCodePre.Add($"; Start of Curve {curveIndex + 1} from Layer {layerIndex + 1}");

                    // Prime move (no extrusion) to start point
                    double fPrime = speedVals.Count > 0 ? speedVals[0] : _psRepr;
                    gCodePre.Add($"G1 F{Gc.FG(fPrime)} X{Gc.F2(startPt.X)} Y{Gc.F2(startPt.Y)} Z{Gc.F2(startPt.Z)} E0.00");

                    if (fPrime < minPrintF) minPrintF = fPrime;
                    if (fPrime > maxPrintF) maxPrintF = fPrime;

                    Point3d prevPt = startPt;

                    // Printing moves
                    for (int i = 1; i < pts.Count; i++)
                    {
                        Point3d pt = pts[i];
                        double d = prevPt.DistanceTo(pt);
                        double lhLocal = layerHVals[i];
                        double fLocal = speedVals[i] > 0 ? speedVals[i] : _psRepr;

                        double fx = i < fluxVals.Count ? fluxVals[i] : 1.0;
                        if (double.IsNaN(fx) || fx <= 0) fx = 1.0;

                        // Volume uses layer_w * layer_h
                        // Keep E based on the established nominal width/height/flow model;
                        // packed print_vol is an analytical reference only in this phase.
                        double segVolMm3 = (_layerW * lhLocal * d) * fx;
                        totalVolumeMm3 += segVolMm3;

                        // Extrusion E uses V/A_fil (relative)
                        double extr = aFil > 0 ? segVolMm3 / aFil : 0.0;

                        gCodePre.Add($"G1 F{Gc.FG(fLocal)} X{Gc.F2(pt.X)} Y{Gc.F2(pt.Y)} Z{Gc.F2(pt.Z)} E{Gc.F5(extr)}");

                        printLenMm += d;
                        if (fLocal > 0)
                            printTimeMinAcc += d / fLocal;

                        if (fLocal < minPrintF) minPrintF = fLocal;
                        if (fLocal > maxPrintF) maxPrintF = fLocal;

                        prevPt = pt;
                    }

                    prevEndPt = prevPt;
                    prevPath = path;
                    gCodePre.Add($"; End of Curve {curveIndex + 1} from Layer {layerIndex + 1}");

                    // End-of-layer hop
                    bool isLastInLayer =
                        branchIndex == nPaths - 1 ||
                        Gc.LogicalLayerIndex(paths[branchIndex + 1], layerPathIndex) != layerIndex;

                    bool continuesToNext = false;
                    if (branchIndex + 1 < nPaths)
                    {
                        GH_Path nextPath = paths[branchIndex + 1];
                        IList<Point3d> nextPoints = _printingPoints.Branch(nextPath);
                        continuesToNext = nextPoints != null && nextPoints.Count > 0 &&
                            IsContinuousJoin(path, prevEndPt.Value, nextPath, nextPoints[0]);
                    }

                    if (ZHopV > 0 && isLastInLayer && !continuesToNext)
                    {
                        double targetUpZ = prevEndPt.Value.Z + ZHopV;
                        gCodePre.Add($"G0 F{Gc.FG(_zHopSpeed)} Z{Gc.F2(targetUpZ)} ; Z-hop up (end of layer)");
                        zhopUpMm += ZHopV;
                        hoppedAfterPrevCurve = true;
                    }
                }

                // ---- Time estimation + summary ----------------------------------
                double travelTimeMin = _travelSpeed > 0 ? travelLenMm / _travelSpeed : 0.0;
                double zhopTimeMin   = _zHopSpeed > 0 ? (zhopUpMm + zhopDownMm) / _zHopSpeed : 0.0;
                double totalTimeMin  = printTimeMinAcc + travelTimeMin + zhopTimeMin;

                if (double.IsPositiveInfinity(minPrintF))
                    printingInfo.Add($";Printing speed (G1 F): {Gc.F0(_psRepr)} [mm/min]");
                else if (Math.Abs(maxPrintF - minPrintF) < 0.5)
                    printingInfo.Add($";Printing speed (G1 F): {Gc.F0(minPrintF)} [mm/min]");
                else
                    printingInfo.Add($";Printing speed (G1 F): {Gc.F0(minPrintF)} to {Gc.F0(maxPrintF)} [mm/min]");

                Layers     = layersSet.Count;
                PVolumeCm3 = Math.Round(totalVolumeMm3 / 1000.0, 2);
                double pVolumeM3 = totalVolumeMm3 / 1e9;
                double pMassKg = Math.Round(_density * pVolumeM3, 2);
                PPathLenMm = Math.Round(printLenMm, 2);
                PTimeMin   = Math.Round(totalTimeMin, 2);
                double pTimeH = Math.Round(PTimeMin / 60.0, 2);

                printingInfo.AddRange(new[]
                {
                    ";--- SUMMARY ---",
                    $";Printed volume: {Gc.F2(totalVolumeMm3 / 1000.0)} cm3",
                    $";Estimated mass: {Gc.Py2(pMassKg)} kg",
                    $";Total time: {Gc.Py2(PTimeMin)} min | {Gc.Py2(pTimeH)} h",
                    $";Print path length (E on): {Gc.F2(printLenMm)} mm",
                    $";XY travel length (G0): {Gc.F2(travelLenMm)} mm",
                    $";Z-hop up: {Gc.F2(zhopUpMm)} mm | down: {Gc.F2(zhopDownMm)} mm",
                });

                Gc.AppendKpiStats(_printKpi, printingInfo, _owner);

                // ---- Final output assembly (single file, branch {0}) -------------
                var gCode = new List<string>();
                gCode.AddRange(printingInfo);
                gCode.AddRange(startGcode);
                gCode.AddRange(gCodePre);
                gCode.AddRange(endGcode);

                OutputGcodeTree = Gc.ListToDataTree(new List<List<string>> { gCode });
            }

            private bool IsContinuousJoin(
                GH_Path previousPath,
                Point3d previousEnd,
                GH_Path currentPath,
                Point3d currentStart)
            {
                int previousStroke = WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, previousPath);
                int currentStroke = WasperGcodeTreeUtil.StrokeIdAt(_strokeIds, currentPath);
                double tolerance = Rhino.RhinoMath.ZeroTolerance;
                return previousStroke >= 0 && previousStroke == currentStroke &&
                    previousEnd.DistanceToSquared(currentStart) <= tolerance * tolerance;
            }

            /// <summary>
            /// FDM per-branch resolution (python rules): default is repr broadcast;
            /// tree branch overrides with invalid entries replaced by repr; scalar
            /// broadcasts; mismatched length warns and broadcasts the first value.
            /// </summary>
            private List<double> ResolveFdmBranch(
                GH_Path path, int nPoints, DataTree<double> tree,
                double? scalar, double repr, string paramName)
            {
                var vals = Enumerable.Repeat(repr, nPoints).ToList();

                if (tree != null && tree.PathExists(path))
                {
                    var raw = tree.Branch(path);
                    var parsed = raw
                        .Select(v => (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) ? repr : v)
                        .ToList();

                    if (parsed.Count == 1)
                        return Enumerable.Repeat(parsed[0], nPoints).ToList();
                    if (parsed.Count == nPoints)
                        return parsed;
                    if (parsed.Count > 0)
                    {
                        Warn($"Branch {path} of '{paramName}' mismatch; using first value for whole curve.");
                        return Enumerable.Repeat(parsed[0], nPoints).ToList();
                    }
                    return vals;
                }

                if (scalar.HasValue)
                    return Enumerable.Repeat(scalar.Value, nPoints).ToList();

                return vals;
            }

            private void Warn(string msg) =>
                _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);

            private void Remark(string msg) =>
                _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, msg);
        }
    }
}
