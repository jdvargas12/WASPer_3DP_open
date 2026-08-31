// wsp_Pp20_Construct WASPer Path.cs
// WASPer_3DP - Subcategory: 4.0_Print Paths
//
// Constructs or repairs a packed WASPer Print Path from editable Grasshopper trees.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP;

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public sealed class wsp_Pp20_Construct_WASPer_Path : GH_Component, IGH_VariableParameterComponent
    {
        private readonly string _versionTag;
        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc10_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc10_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;


        private enum Input
        {
            WspPath,
            Planes,
            LayerPlanes,
            Flows,
            LayerH,
            LayerW,
            NozzleDiam,
            PrintSpeed
        }

        private static readonly string[] InputNames =
        {
            "wsp_path",
            "pt_planes",
            "la_planes",
            "flows",
            "layer_h",
            "layer_w",
            "nozzle_diam",
            "print_speed"
        };


        public wsp_Pp20_Construct_WASPer_Path()
            : base(
                "wsp_Pp20_Construct WASPer Path",
                "Construct WPath",
                "Constructs a packed WASPer Print Path (wsp_path) from canonical point planes, optional authoritative layer reference planes, flow, height, width, nozzle, and speed data. Point-plane origins define path locations; layer-plane paths end at the logical-layer dimension and contain one plane.\n\n" +
                "Use this component to modify or rebuild key wsp_path parameters mid-workflow without running analysis or visualization.\n\n" +
                "Disconnected overrides preserve existing wsp_path values. No fitted or reconstructed layer reference planes are generated when la_planes is absent.\n\n" +
                "Right-click Show all outputs to inspect the common outgoing-path debug fields.\n\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component when supplying a reference wsp_path.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("A7387736-DC8D-4935-86BE-E19D14B13ACF");
        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc10_Construct WASPer Path.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Optional reference WASPer Print Path. This input is always visible. Disconnected optional override inputs preserve its existing fields; connected override inputs replace the corresponding path information. Please use the Pp01 WASPer Path from Curves before using this component when supplying a reference wsp_path.",
                GH_ParamAccess.item);
            p[0].Optional = true;
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Planes Input", Toggle(Input.Planes), true, Has(Input.Planes));
            Menu_AppendItem(menu, "Layer Reference Planes Input", Toggle(Input.LayerPlanes), true, Has(Input.LayerPlanes));
            Menu_AppendItem(menu, "Flows Input", Toggle(Input.Flows), true, Has(Input.Flows));
            Menu_AppendItem(menu, "Layer Height Input", Toggle(Input.LayerH), true, Has(Input.LayerH));
            Menu_AppendItem(menu, "Layer Width Input", Toggle(Input.LayerW), true, Has(Input.LayerW));
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Nozzle Diameter Input", Toggle(Input.NozzleDiam), true, Has(Input.NozzleDiam));
            Menu_AppendItem(menu, "Print Speed Input", Toggle(Input.PrintSpeed), true, Has(Input.PrintSpeed));
            Menu_AppendSeparator(menu);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Summary of how input trees were matched and which optional fields were packed.",
                        OutputCatalog);
                    ExpireSolution(true);
                });
        }

        private EventHandler Toggle(Input input)
        {
            return (sender, args) =>
            {
                RecordUndoEvent($"Toggle {InputNames[(int)input]}");
                if (Has(input)) Remove(input);
                else Add(input);
                Changed();
            };
        }

        private IGH_Param Add(Input input)
        {
            IGH_Param existing = Find(input);
            if (existing != null) return existing;

            IGH_Param param = New(input);
            Params.RegisterInputParam(param, InsertIndex(input));
            return param;
        }

        private void Remove(Input input)
        {
            IGH_Param param = Find(input);
            if (param != null)
                Params.UnregisterInputParameter(param, true);
        }

        private int InsertIndex(Input input)
        {
            int target = (int)input;
            for (int i = 0; i < Params.Input.Count; i++)
            {
                int other = IndexOf(Params.Input[i]);
                if (other > target) return i;
            }
            return Params.Input.Count;
        }

        private static IGH_Param New(Input input)
        {
            switch (input)
            {
                case Input.WspPath:
                    return new Param_GenericObject
                    {
                        Name = "wsp_path",
                        NickName = "wsp_path",
                        Description = "Optional reference WASPer Print Path. This input is always visible. Disconnected optional override inputs preserve its existing fields; connected override inputs replace the corresponding path information. Please use the Pp01 WASPer Path from Curves before using this component when supplying a reference wsp_path.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                case Input.Planes:
                    return new Param_Plane
                    {
                        Name = "pt_planes",
                        NickName = "pt_planes",
                        Description = "Optional canonical path-plane tree. When connected, it overrides wsp_path.PtPlanes. Each plane origin is the corresponding printing point; without wsp_path, this input is required.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                case Input.Flows:
                    return new Param_Number
                    {
                        Name = "flows",
                        NickName = "flows",
                        Description = "Optional flow multiplier tree/scalar. When visible and connected, it overrides wsp_path.Flows. If absent everywhere, flow defaults to 1.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                case Input.LayerPlanes:
                    return new Param_Plane
                    {
                        Name = "la_planes",
                        NickName = "la_planes",
                        Description = "Optional authoritative reference-plane tree, one plane per logical layer on canonical paths ending at the layer dimension (for example {layer}). When connected, it overrides wsp_path.LayerPlanes. Missing layers remain absent; Pp20 does not generate fitted replacements.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                case Input.LayerH:
                    return new Param_Number
                    {
                        Name = "layer_h",
                        NickName = "layer_h",
                        Description = "Optional layer height tree/scalar. When visible and connected, it overrides wsp_path.LayerH. Required if no reference path LayerH exists.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                case Input.LayerW:
                    return new Param_Number
                    {
                        Name = "layer_w",
                        NickName = "layer_w",
                        Description = "Optional nominal/base bead width tree/scalar. When visible and connected, it overrides wsp_path.LayerW. Otherwise preserves path LayerW or defaults to layer_h * 2.5.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                case Input.NozzleDiam:
                    return new Param_Number
                    {
                        Name = "nozzle_diam",
                        NickName = "nozzle",
                        Description = "Optional nozzle diameter. When visible and connected with a positive value, it overrides wsp_path.NozzleDiam.",
                        Access = GH_ParamAccess.item,
                        Optional = true
                    };
                case Input.PrintSpeed:
                    return new Param_Number
                    {
                        Name = "print_speed",
                        NickName = "speed",
                        Description = "Optional print speed tree/scalar. When visible and connected, it overrides wsp_path.PrintSpeed and every supplied value must be positive and finite. When absent, Pp20 preserves a valid incoming PrintSpeed; if no valid speed exists, print_speed remains unpacked.",
                        Access = GH_ParamAccess.tree,
                        Optional = true
                    };
                default:
                    return null;
            }
        }

        private void Changed()
        {
            Params.OnParametersChanged();
            ExpireSolution(true);
        }

        private bool Has(Input input) => Find(input) != null;
        private IGH_Param Find(Input input) => Params.Input.FirstOrDefault(param => param.Name == InputNames[(int)input]);
        private static int IndexOf(IGH_Param param) => Array.IndexOf(InputNames, param.Name);
        private int InputIndex(Input input) => Params.Input.FindIndex(param => param.Name == InputNames[(int)input]);

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open). The obsolete-input
            // cleanup still runs after base.Read(), matching its original position.
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the legacy boolean ShowAllOutputsKey. Map "Show all outputs" = true to every
            // bit set, so old files keep showing everything they used to.
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Summary of how input trees were matched and which optional fields were packed.",
                OutputCatalog);

            bool result = base.Read(reader);

            IGH_Param obsoletePoints = Params.Input.FirstOrDefault(
                param => string.Equals(param.Name, "p_points", StringComparison.Ordinal));
            if (obsoletePoints != null)
                Params.UnregisterInputParameter(obsoletePoints, true);

            return result;
        }

        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index) => false;
        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index) => false;
        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index) => null;
        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index) => false;
        void IGH_VariableParameterComponent.VariableParameterMaintenance() { }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Constructed WASPer Print Path containing canonical pt_planes, flows, layer_h, layer_w, flow-adjusted layer_wf, optional nozzle_diam, optional print_speed, and print_vol. Path points are derived from plane origins.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Summary of how input trees were matched and which optional fields were packed.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            WasperPrintPath incomingPath = null;
            bool hasIncomingPath = WasperGcodeTreeUtil.TryGetPrintPath(da, InputIndex(Input.WspPath), out incomingPath) && incomingPath != null;

            GH_Structure<GH_Point> pointTree = null;
            GH_Structure<GH_Plane> planeInput = null;
            GH_Structure<GH_Plane> layerPlaneInput = null;
            GH_Structure<GH_Number> flowInput = null;
            GH_Structure<GH_Number> heightInput = null;
            GH_Structure<GH_Number> widthInput = null;
            GH_Structure<GH_Number> speedInput = null;

            int planeIndex = InputIndex(Input.Planes);
            int layerPlaneIndex = InputIndex(Input.LayerPlanes);
            int flowIndex = InputIndex(Input.Flows);
            int heightIndex = InputIndex(Input.LayerH);
            int widthIndex = InputIndex(Input.LayerW);
            int nozzleIndex = InputIndex(Input.NozzleDiam);
            int speedIndex = InputIndex(Input.PrintSpeed);

            bool hasPlaneOverride = planeIndex >= 0 && da.GetDataTree(planeIndex, out planeInput) && HasData(planeInput);
            bool hasLayerPlaneOverride =
                layerPlaneIndex >= 0 &&
                da.GetDataTree(layerPlaneIndex, out layerPlaneInput) &&
                HasData(layerPlaneInput);
            bool hasFlowOverride = flowIndex >= 0 && da.GetDataTree(flowIndex, out flowInput) && HasData(flowInput);
            bool hasHeightOverride = heightIndex >= 0 && da.GetDataTree(heightIndex, out heightInput) && HasData(heightInput);
            bool hasWidthOverride = widthIndex >= 0 && da.GetDataTree(widthIndex, out widthInput) && HasData(widthInput);
            bool speedConnected =
                speedIndex >= 0 &&
                Params.Input[speedIndex].SourceCount > 0;
            bool hasSpeedOverride =
                speedConnected &&
                da.GetDataTree(speedIndex, out speedInput) &&
                HasData(speedInput);

            string planeSource;
            if (hasPlaneOverride)
            {
                planeSource = "pt_planes input override";
            }
            else if (hasIncomingPath && incomingPath.HasPlanes)
            {
                planeInput = WasperGcodeTreeUtil.ToPlaneStructure(incomingPath.PtPlanes);
                planeSource = "wsp_path.PtPlanes";
            }
            else
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Supply wsp_path or pt_planes. Plane origins are the canonical path points.");
                return;
            }

            bool hasPlanes = HasData(planeInput);
            if (!hasPlanes || !TryBuildPointsFromPlanes(planeInput, out pointTree))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "pt_planes must contain at least one valid plane; plane origins define the path.");
                return;
            }

            bool hasFlows = hasFlowOverride;
            string flowSource = "flows input override";
            if (!hasFlowOverride && hasIncomingPath && incomingPath.HasFlows)
            {
                flowInput = WasperGcodeTreeUtil.ToNumberStructure(incomingPath.Flows);
                hasFlows = HasData(flowInput);
                flowSource = "wsp_path.Flows";
            }
            else if (!hasFlowOverride)
            {
                flowSource = "default 1.0";
            }

            bool hasHeights = hasHeightOverride;
            string heightSource = "layer_h input override";
            if (!hasHeightOverride && hasIncomingPath && incomingPath.HasLayerH)
            {
                heightInput = WasperGcodeTreeUtil.ToNumberStructure(incomingPath.LayerH);
                hasHeights = HasData(heightInput);
                heightSource = "wsp_path.LayerH";
            }
            if (!hasHeights)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "layer_h is required when the reference wsp_path does not contain LayerH, because layer_w defaults to layer_h * 2.5 and print_vol depends on height.");
                return;
            }

            bool hasWidths = hasWidthOverride;
            string widthSource = "layer_w input override";
            if (!hasWidthOverride && hasIncomingPath && incomingPath.HasLayerW)
            {
                widthInput = WasperGcodeTreeUtil.ToNumberStructure(incomingPath.LayerW);
                hasWidths = HasData(widthInput);
                widthSource = "wsp_path.LayerW";
            }
            else if (!hasWidthOverride)
            {
                widthSource = "default layer_h * 2.5";
            }

            bool hasSpeeds = hasSpeedOverride;
            string speedSource = "print_speed input override";
            if (!hasSpeedOverride && hasIncomingPath && incomingPath.HasPrintSpeed)
            {
                if (ContainsPositiveFiniteValue(incomingPath.PrintSpeed))
                {
                    speedInput = WasperGcodeTreeUtil.ToNumberStructure(incomingPath.PrintSpeed);
                    hasSpeeds = HasData(speedInput);
                    speedSource = "wsp_path.PrintSpeed";
                }
                else
                {
                    speedInput = null;
                    hasSpeeds = false;
                    speedSource = "not packed";
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        "The incoming wsp_path contains only non-positive/non-finite print_speed placeholders. " +
                        "Pp20 treats this as speed not set and leaves PrintSpeed unpacked.");
                }
            }
            else if (!hasSpeedOverride)
            {
                speedSource = "not packed";
            }

            double nozzleDiamInput = 0.0;
            bool nozzleConnected = nozzleIndex >= 0 && Params.Input[nozzleIndex].SourceCount > 0;
            bool hasNozzleOverride = nozzleIndex >= 0 && da.GetData(nozzleIndex, ref nozzleDiamInput) && IsPositiveFinite(nozzleDiamInput);
            if (nozzleConnected && !hasNozzleOverride)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "nozzle_diam was supplied but is not positive/finite; existing wsp_path.NozzleDiam is preserved when available.");
            double? resolvedNozzle = hasNozzleOverride
                ? nozzleDiamInput
                : incomingPath?.NozzleDiam;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            tol = Math.Max(tol, 1e-9);

            var outPlanes = new DataTree<Plane>();
            var outFlows = new DataTree<double>();
            var outHeights = new DataTree<double>();
            var outWidths = new DataTree<double>();
            var outWidthFlow = new DataTree<double>();
            var outPrintVol = new DataTree<double>();
            var outSpeeds = hasSpeeds ? new DataTree<double>() : null;

            int branchScalarFlows = 0;
            int branchScalarHeights = 0;
            int branchScalarWidths = 0;
            int branchScalarSpeeds = 0;

            for (int bi = 0; bi < pointTree.PathCount; bi++)
            {
                GH_Path path = pointTree.Paths[bi];
                IList pointBranch = pointTree.get_Branch(path);
                int count = pointBranch?.Count ?? 0;
                if (count == 0) continue;

                IList planeBranch = null;
                if (hasPlanes && !TryResolveBranch(planeInput, path, bi, count, out planeBranch, out string planeMode))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"pt_planes cannot be matched to branch {path}. Use one plane per point, one plane per branch, or a matching tree.");
                    return;
                }

                if (!TryResolveNumberBranch(flowInput, hasFlows, path, bi, count, 1.0, true, "flows", out var flows, out string flowMode))
                    return;
                if (!TryResolveNumberBranch(heightInput, true, path, bi, count, 0.0, false, "layer_h", out var heights, out string heightMode))
                    return;
                if (!TryResolveLayerWidthBranch(widthInput, hasWidths, path, bi, count, heights, out var widths, out string widthMode))
                    return;
                double[] speeds = null;
                string speedMode = "missing";
                if (hasSpeeds && !TryResolveNumberBranch(speedInput, true, path, bi, count, 0.0, false, "print_speed", out speeds, out speedMode))
                    return;

                if (flowMode == "branch-scalar" || flowMode == "global-scalar") branchScalarFlows++;
                if (heightMode == "branch-scalar" || heightMode == "global-scalar") branchScalarHeights++;
                if (widthMode == "branch-scalar" || widthMode == "global-scalar" || widthMode == "default layer_h*2.5") branchScalarWidths++;
                if (hasSpeeds && (speedMode == "branch-scalar" || speedMode == "global-scalar")) branchScalarSpeeds++;

                var widthFlowValues = new double[count];

                for (int i = 0; i < count; i++)
                {
                    if (!(pointBranch[i] is GH_Point ghPoint) || !ghPoint.Value.IsValid)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"The path location derived from pt_planes is invalid at branch {path}, item {i}.");
                        return;
                    }

                    Point3d point = ghPoint.Value;
                    Plane plane = Plane.Unset;
                    if (hasPlanes)
                    {
                        int pi = planeBranch.Count == 1 ? 0 : i;
                        if (!(planeBranch[pi] is GH_Plane ghPlane) || !ghPlane.Value.IsValid)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"pt_planes branch {path} contains an invalid plane at item {pi}.");
                            return;
                        }
                        plane = ghPlane.Value;
                    }

                    double flow = flows[i];
                    double height = heights[i];
                    double width = widths[i];
                    if (!IsPositiveFinite(height))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"layer_h must be positive and finite. Bad value at branch {path}, item {i}.");
                        return;
                    }
                    if (!IsPositiveFinite(width))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"layer_w/default width must be positive and finite. Bad value at branch {path}, item {i}.");
                        return;
                    }
                    if (!IsFinite(flow))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"flows must be finite. Bad value at branch {path}, item {i}.");
                        return;
                    }

                    outPlanes.Add(plane, path);
                    outFlows.Add(flow, path);
                    outHeights.Add(height, path);
                    outWidths.Add(width, path);
                    double widthFlow = EstimateFlowAdjustedWidth(width, height, flow, tol);
                    widthFlowValues[i] = widthFlow;
                    outWidthFlow.Add(widthFlow, path);

                    if (hasSpeeds)
                    {
                        double speed = speeds[i];
                        if (!IsPositiveFinite(speed))
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"print_speed must be positive and finite when supplied. Bad value at branch {path}, item {i}.");
                            return;
                        }
                        outSpeeds.Add(speed, path);
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && pointBranch[i - 1] is GH_Point previous && pointBranch[i] is GH_Point current)
                    {
                        double wf = widthFlowValues[i];
                        double h = heights[i];
                        double length = previous.Value.DistanceTo(current.Value);
                        if (wf > tol && h > tol && double.IsFinite(length))
                        {
                            double area = h * (wf - h) + Math.PI * h * h / 4.0;
                            if (area > 0.0 && double.IsFinite(area)) volume = length * area;
                        }
                    }
                    outPrintVol.Add(volume, path);
                }
            }

            DataTree<Plane> candidateLayerPlanes = hasLayerPlaneOverride
                ? WasperGcodeTreeUtil.ToPlaneTree(layerPlaneInput)
                : incomingPath?.LayerPlanes;
            int outputPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(
                outPlanes.Paths.ToList());
            DataTree<Plane> resolvedLayerPlanes =
                WasperGcodeTreeUtil.FilterLayerPlanes(
                    candidateLayerPlanes,
                    outPlanes.Paths,
                    outputPrefix);

            var pathObj = new WasperPrintPath(
                WasperGcodeTreeUtil.ToPointTree(pointTree),
                outPlanes,
                outFlows,
                outHeights,
                printSpeed: outSpeeds,
                printLoc: incomingPath?.PrintLoc,
                printGlob: incomingPath?.PrintGlob,
                supportPts: incomingPath?.SupportPts,
                supportVects: incomingPath?.SupportVects,
                angles: incomingPath?.Angles,
                contactWidths: incomingPath?.ContactWidths,
                riskMaterial: incomingPath?.RiskMaterial,
                riskComb: incomingPath?.RiskComb,
                load: incomingPath?.Load,
                capacity: incomingPath?.Capacity,
                nozzleDiam: resolvedNozzle,
                dRatio: incomingPath?.DRatio,
                dLoaded: incomingPath?.DLoaded,
                bendRatio: incomingPath?.BendRatio,
                spanClass: incomingPath?.SpanClass,
                spanLen: incomingPath?.SpanLen,
                collapsed: incomingPath?.Collapsed,
                cascade: incomingPath?.Cascade,
                collapseGen: incomingPath?.CollapseGen,
                layerW: outWidths,
                layerWf: outWidthFlow,
                printVol: outPrintVol,
                torn: incomingPath?.Torn,
                interfaceRatio: incomingPath?.InterfaceRatio,
                overturnRatio: incomingPath?.OverturnRatio,
                failureFlags: incomingPath?.FailureFlags,
                travelSpeed: incomingPath?.TravelSpeed,
                zHop: incomingPath?.ZHop,
                zHopSpeed: incomingPath?.ZHopSpeed,
                motionPlan: incomingPath?.MotionPlan,
                pathRoles: WasperGcodeTreeUtil.FilterPathRoles(
                    incomingPath?.PathRoles,
                    outPlanes?.Paths),
                layerPlanes: resolvedLayerPlanes);

            var summary = new StringBuilder();
            summary.AppendLine("wsp_Pp20_Construct WASPer Path");
            summary.AppendLine(hasIncomingPath ? "reference: wsp_path input" : "reference: none");
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "plane locations: {0}", pathObj.PointCount));
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "branches: {0}", pathObj.BranchCount));
            summary.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "pt_planes: {0}; {1} path locations derived from plane origins",
                planeSource,
                pointTree.DataCount));
            summary.AppendLine(
                $"la_planes: {(hasLayerPlaneOverride ? "input override" : "preserved from wsp_path")} " +
                $"({resolvedLayerPlanes?.DataCount ?? 0} authoritative layer reference plane(s))");
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "flows: {0}{1}", flowSource, hasFlows ? string.Format(CultureInfo.InvariantCulture, " ({0} scalar/broadcast branch(es))", branchScalarFlows) : ""));
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "layer_h: {0} ({1} scalar/broadcast branch(es))", heightSource, branchScalarHeights));
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture, "layer_w: {0}{1}", widthSource, (hasWidths || widthSource.StartsWith("default", StringComparison.Ordinal)) ? string.Format(CultureInfo.InvariantCulture, " ({0} scalar/broadcast/default branch(es))", branchScalarWidths) : ""));
            summary.AppendLine("layer_wf: recomputed from resolved layer_w, layer_h, and flows");
            summary.AppendLine("print_vol: recomputed from resolved layer_wf and segment lengths");
            summary.AppendLine(resolvedNozzle.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "nozzle_diam: {0:R}{1}", resolvedNozzle.Value, hasNozzleOverride ? " (input override)" : " (preserved from wsp_path)")
                : "nozzle_diam: not packed");
            summary.AppendLine(hasSpeeds
                ? string.Format(CultureInfo.InvariantCulture, "print_speed: {0} ({1} scalar/broadcast branch(es))", speedSource, branchScalarSpeeds)
                : "print_speed: not packed");

            da.SetData(0, new WasperPrintPathGoo(pathObj));
            da.SetData(1, summary.ToString().TrimEnd());
            WasperPathDebugOutputs.SetCore(da, this, pathObj);
            Message = $"{_versionTag} | {pathObj.PointCount} planes";
        }

        private static bool HasData<T>(GH_Structure<T> tree) where T : IGH_Goo
        {
            return tree != null && tree.PathCount > 0 && tree.DataCount > 0;
        }

        private static bool ContainsPositiveFiniteValue(DataTree<double> tree)
        {
            if (tree == null || tree.BranchCount == 0)
                return false;

            foreach (double value in tree.AllData())
            {
                if (IsPositiveFinite(value))
                    return true;
            }

            return false;
        }

        private bool TryBuildPointsFromPlanes(GH_Structure<GH_Plane> planeTree, out GH_Structure<GH_Point> pointTree)
        {
            pointTree = new GH_Structure<GH_Point>();
            if (!HasData(planeTree))
                return false;

            for (int bi = 0; bi < planeTree.PathCount; bi++)
            {
                GH_Path path = planeTree.Paths[bi];
                IList branch = planeTree.get_Branch(path);
                if (branch == null) continue;

                for (int i = 0; i < branch.Count; i++)
                {
                    if (!(branch[i] is GH_Plane ghPlane) || !ghPlane.Value.IsValid)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"pt_planes branch {path} contains an invalid plane at item {i}; cannot derive the path location from its origin.");
                        return false;
                    }

                    pointTree.Append(new GH_Point(ghPlane.Value.Origin), path);
                }
            }

            return pointTree.DataCount > 0;
        }

        private bool TryResolveBranch<T>(
            GH_Structure<T> tree,
            GH_Path path,
            int branchIndex,
            int targetCount,
            out IList branch,
            out string mode) where T : IGH_Goo
        {
            branch = null;
            mode = "missing";
            if (!HasData(tree)) return false;

            if (tree.PathExists(path))
            {
                branch = tree.get_Branch(path);
                mode = branch != null && branch.Count == targetCount ? "matching-tree" : "branch-scalar";
            }
            else if (tree.PathCount == 1)
            {
                branch = tree.get_Branch(tree.Paths[0]);
                mode = branch != null && branch.Count == 1 ? "global-scalar" : "single-branch";
            }
            else if (branchIndex < tree.PathCount)
            {
                branch = tree.get_Branch(tree.Paths[branchIndex]);
                mode = branch != null && branch.Count == targetCount ? "branch-index" : "branch-scalar";
            }

            return branch != null && (branch.Count == targetCount || branch.Count == 1);
        }

        private bool TryResolveNumberBranch(
            GH_Structure<GH_Number> tree,
            bool hasTree,
            GH_Path path,
            int branchIndex,
            int targetCount,
            double fallback,
            bool allowMissing,
            string label,
            out double[] values,
            out string mode)
        {
            values = new double[targetCount];
            mode = "default";
            if (!hasTree)
            {
                if (!allowMissing)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{label} is required.");
                    return false;
                }
                for (int i = 0; i < targetCount; i++) values[i] = fallback;
                return true;
            }

            if (!TryResolveBranch(tree, path, branchIndex, targetCount, out IList branch, out mode))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{label} cannot be matched to branch {path}. Use one value per point, one value per branch, one global scalar, or a matching tree.");
                return false;
            }

            if (branch.Count == 1)
            {
                double v = NumberValue(branch[0], double.NaN);
                for (int i = 0; i < targetCount; i++) values[i] = v;
                if (mode != "global-scalar") mode = "branch-scalar";
                return true;
            }

            for (int i = 0; i < targetCount; i++) values[i] = NumberValue(branch[i], double.NaN);
            return true;
        }

        private bool TryResolveLayerWidthBranch(
            GH_Structure<GH_Number> widthTree,
            bool hasWidths,
            GH_Path path,
            int branchIndex,
            int targetCount,
            double[] heights,
            out double[] widths,
            out string mode)
        {
            widths = new double[targetCount];
            if (!hasWidths)
            {
                for (int i = 0; i < targetCount; i++) widths[i] = heights[i] * 2.5;
                mode = "default layer_h*2.5";
                return true;
            }

            return TryResolveNumberBranch(widthTree, true, path, branchIndex, targetCount, 0.0, false, "layer_w", out widths, out mode);
        }

        private static double NumberValue(object item, double fallback)
        {
            return item is GH_Number number && double.IsFinite(number.Value) ? number.Value : fallback;
        }

        private static double EstimateFlowAdjustedWidth(double nominalWidth, double height, double flow, double tol)
        {
            if (nominalWidth <= tol || height <= tol || flow <= tol ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = height * (referenceWidth - height)
                + Math.PI * height * height / 4.0;
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static bool IsFinite(double value)
        {
            return double.IsFinite(value);
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0.0 && double.IsFinite(value);
        }
    }
}
